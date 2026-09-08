using OpenCvSharp;

namespace UvssService.Ocr;

/// <summary>India's RTO plate colour scheme, classified straight from the
/// plate crop's own background + text colour (no separate trained model
/// needed):
///
///   White bg, black text   -> private vehicle
///   Yellow bg, black text  -> commercial/transport vehicle
///   Black bg, yellow text  -> commercial self-drive rental vehicle
///   Green bg, white text   -> private electric vehicle (EV)
///   Green bg, yellow text  -> commercial electric vehicle (EV)
///   Red bg, white text     -> temporary registration
///   Blue bg, white text    -> foreign diplomatic/consular/UN vehicle
///
/// Background colour distinguishes most categories on its own; green and
/// black backgrounds are ambiguous between two categories each until the
/// TEXT colour (sampled separately, see SampleForeground) is checked too.</summary>
public enum PlateColorCategory
{
    Unknown,
    PrivateWhite,
    CommercialYellow,
    CommercialSelfDriveBlack,
    ElectricPrivateGreen,
    ElectricCommercialGreen,
    TemporaryRed,
    DiplomaticBlue,
}

public static class PlateColorClassifier
{
    private enum Background { Unknown, White, Yellow, Green, DarkCandidate, Red, Blue }

    public static PlateColorCategory Classify(Mat plateCrop) => Classify(plateCrop, out _);

    /// <summary>Same classification, plus a compact one-line diagnostic
    /// string (background/foreground HSV samples and what each was read
    /// as) -- lets a site engineer see WHY a plate was (mis)classified
    /// instead of just the final label, e.g. when checking how reliably
    /// the text-colour read is holding up across frames of the same
    /// plate.</summary>
    public static PlateColorCategory Classify(Mat plateCrop, out string diagnostics)
    {
        if (plateCrop.Empty() || plateCrop.Rows < 6 || plateCrop.Cols < 6)
        {
            diagnostics = "crop too small/empty";
            return PlateColorCategory.Unknown;
        }

        using var hsv = new Mat();
        Cv2.CvtColor(plateCrop, hsv, ColorConversionCodes.BGR2HSV);

        var (bgHue, bgSat, bgVal) = SampleBackground(hsv);
        var background = ClassifyBackground(bgHue, bgSat, bgVal);
        var bgDesc = $"bg(H={bgHue:F0},S={bgSat:F0},V={bgVal:F0})={background}";

        // Green and black backgrounds each cover two categories (private vs.
        // commercial) that only the TEXT colour tells apart -- everything
        // else is fully determined by the background alone.
        if (background is Background.Green or Background.DarkCandidate)
        {
            var (fgHue, fgSat, fgVal, found) = SampleForeground(hsv);
            var textIsYellow = found && IsYellowish(fgHue, fgSat, fgVal);
            var fgDesc = found
                ? $"fg(H={fgHue:F0},S={fgSat:F0},V={fgVal:F0})={(textIsYellow ? "yellow" : "not-yellow")}"
                : "fg=not found";

            PlateColorCategory result;
            if (background == Background.Green)
            {
                result = textIsYellow ? PlateColorCategory.ElectricCommercialGreen : PlateColorCategory.ElectricPrivateGreen;
            }
            else
            {
                // DarkCandidate: a genuinely black plate has bright yellow
                // text clearly standing out against it -- if no such
                // contrasting foreground was found, this is more likely
                // just underexposed (a badly-lit white/yellow plate looks
                // dark too), so don't guess "black" from darkness alone.
                result = textIsYellow ? PlateColorCategory.CommercialSelfDriveBlack : PlateColorCategory.Unknown;
            }
            diagnostics = $"{bgDesc} {fgDesc} -> {result}";
            return result;
        }

        var final = background switch
        {
            Background.White => PlateColorCategory.PrivateWhite,
            Background.Yellow => PlateColorCategory.CommercialYellow,
            Background.Red => PlateColorCategory.TemporaryRed,
            Background.Blue => PlateColorCategory.DiplomaticBlue,
            _ => PlateColorCategory.Unknown,
        };
        diagnostics = $"{bgDesc} -> {final}";
        return final;
    }

    private static Background ClassifyBackground(double hue, double sat, double val)
    {
        if (sat < 45 && val > 120)
        {
            return Background.White;
        }
        if (val < 70)
        {
            // Dark AND low-colour -- either a genuine black plate or just
            // poor lighting; SampleForeground disambiguates by checking for
            // bright yellow text standing out against it.
            return Background.DarkCandidate;
        }
        if (hue is >= 18 and <= 34 && sat > 60)
        {
            return Background.Yellow;
        }
        if (hue is >= 35 and <= 95 && sat > 45)
        {
            return Background.Green;
        }
        if (hue is >= 100 and <= 135 && sat > 45)
        {
            return Background.Blue;
        }
        if ((hue <= 9 || hue >= 165) && sat > 60)
        {
            return Background.Red;
        }
        return Background.Unknown;
    }

    /// <summary>The plate's TEXT lives mainly in the crop's middle band --
    /// the top/bottom edge strips are almost always plain background colour
    /// regardless of plate length, so sampling just those for the
    /// background avoids the text's own colour skewing the average.</summary>
    private static (double Hue, double Sat, double Val) SampleBackground(Mat hsv)
    {
        var stripHeight = Math.Max(2, hsv.Rows / 6);
        using var topStrip = new Mat(hsv, new Rect(0, 0, hsv.Cols, stripHeight));
        using var bottomStrip = new Mat(hsv, new Rect(0, hsv.Rows - stripHeight, hsv.Cols, stripHeight));
        using var sampled = new Mat();
        Cv2.VConcat(new[] { topStrip, bottomStrip }, sampled);

        Cv2.MeanStdDev(sampled, out var mean, out _);
        return (mean.Val0, mean.Val1, mean.Val2);
    }

    /// <summary>Finds the plate's character-stroke pixels by Otsu-splitting
    /// the crop's middle band on brightness (text is a strong local
    /// light/dark outlier against a comparatively flat background) and
    /// samples their average colour -- the minority cluster is assumed to
    /// be the text, since characters cover far less area than the
    /// background. Returns Found=false if no clear minority cluster
    /// emerged (e.g. a blank/unreadable crop), so callers don't guess a
    /// text colour that isn't really there.</summary>
    private static (double Hue, double Sat, double Val, bool Found) SampleForeground(Mat hsv)
    {
        var midStart = hsv.Rows / 4;
        var midHeight = Math.Max(2, Math.Min(hsv.Rows / 2, hsv.Rows - midStart));
        using var midBand = new Mat(hsv, new Rect(0, midStart, hsv.Cols, midHeight));

        var channels = Cv2.Split(midBand);
        using var hueCh = channels[0];
        using var satCh = channels[1];
        using var valCh = channels[2];

        using var mask = new Mat();
        Cv2.Threshold(valCh, mask, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);

        var total = mask.Rows * mask.Cols;
        var whiteCount = Cv2.CountNonZero(mask);
        // Text is normally the minority cluster -- pick whichever side of
        // the Otsu split is smaller as the presumed text mask.
        var textMask = whiteCount * 2 <= total ? mask : InvertedMask(mask);
        try
        {
            var textCount = Cv2.CountNonZero(textMask);
            if (textCount == 0 || textCount > total * 0.6)
            {
                // No real minority cluster (near-uniform crop, or the
                // "split" just picked up noise) -- nothing to report.
                return (0, 0, 0, false);
            }

            Cv2.MeanStdDev(midBand, out var fgMean, out _, textMask);
            return (fgMean.Val0, fgMean.Val1, fgMean.Val2, true);
        }
        finally
        {
            if (!ReferenceEquals(textMask, mask))
            {
                textMask.Dispose();
            }
        }
    }

    private static Mat InvertedMask(Mat mask)
    {
        var inverted = new Mat();
        Cv2.BitwiseNot(mask, inverted);
        return inverted;
    }

    private static bool IsYellowish(double hue, double sat, double val) =>
        hue is >= 15 and <= 40 && sat > 60 && val > 80;

    public static string CategoryLabel(PlateColorCategory category) => category switch
    {
        PlateColorCategory.PrivateWhite => "Private Vehicle",
        PlateColorCategory.CommercialYellow => "Commercial Vehicle",
        PlateColorCategory.CommercialSelfDriveBlack => "Commercial (Self-Drive Rental)",
        PlateColorCategory.ElectricPrivateGreen => "Electric Vehicle (Private)",
        PlateColorCategory.ElectricCommercialGreen => "Electric Vehicle (Commercial)",
        PlateColorCategory.TemporaryRed => "Temporary Registration",
        PlateColorCategory.DiplomaticBlue => "Diplomatic Vehicle",
        _ => "Unknown",
    };
}
