using System.Text;
using Quaver.API.Maps;

namespace qua3osu.Osu.BeatmapSections
{
    public class TimingPointsSection : BeatmapSection
    {
        private const float MinOsuSliderVelocity = 0.01f;
        private const float MaxOsuSliderVelocity = 10f;
        private const double FloatTolerance = 0.000001;

        public List<TimingPoint> TimingPoints;

        public TimingPointsSection(Qua qua)
        {
            if (qua.BPMDoesNotAffectScrollVelocity)
            {
                qua.SortTimingPoints();
                qua.SortSliderVelocities();
                qua.DenormalizeSVs();
            }

            TimingPoints = BuildTimingPoints(qua);
        }

        protected override string SectionTitle => "TimingPoints";

        public override string ToString()
        {
            var lines = new StringBuilder();
            lines.AppendLine(FormatTitle());
            TimingPoints.ForEach(tp => lines.AppendLine(tp.ToString()));
            return lines.ToString();
        }

        private static List<TimingPoint> BuildTimingPoints(Qua qua)
        {
            const int largeMeter = 1000;
            var timingPoints = qua.TimingPoints.OrderBy(x => x.StartTime).ToList();
            var sliderVelocities = qua.SliderVelocities.OrderBy(x => x.StartTime).ToList();

            if (timingPoints.Count == 0)
                return sliderVelocities.Select(sv => new TimingPoint(sv)).ToList();

            var barlineResyncEndTime = qua.HitObjects.Count > 0
                ? qua.HitObjects.Max(x => Math.Max(x.StartTime, x.EndTime))
                : timingPoints.Max(x => x.StartTime);

            var output = new List<TimingPoint>();
            var timingPointIndex = 0;
            var sliderVelocityIndex = 0;

            // Track the source map state separately from the BPM currently written to osu!.
            var currentBpm = timingPoints[0].Bpm;
            var currentOutputBpm = (double) currentBpm;
            var currentTimingPointStart = timingPoints[0].StartTime;
            var currentMeter = (int) timingPoints[0].Signature;
            var currentSliderVelocity = 1f;
            var hasSliderVelocity = false;
            var hasOutputTimingPoint = false;
            double? nextBarlineResyncTime = null;

            // Merge BPM, SV, and synthetic barline resync changes into one chronological event stream.
            while (timingPointIndex < timingPoints.Count ||
                   sliderVelocityIndex < sliderVelocities.Count ||
                   nextBarlineResyncTime != null)
            {
                var nextTimingPointTime = timingPointIndex < timingPoints.Count
                    ? timingPoints[timingPointIndex].StartTime
                    : double.PositiveInfinity;
                var nextSliderVelocityTime = sliderVelocityIndex < sliderVelocities.Count
                    ? sliderVelocities[sliderVelocityIndex].StartTime
                    : double.PositiveInfinity;
                var nextResyncTime = nextBarlineResyncTime ?? double.PositiveInfinity;
                var time = Math.Min(Math.Min(nextTimingPointTime, nextSliderVelocityTime), nextResyncTime);
                var sourceTimingPointChanged = false;
                var barlineResyncDue = nextBarlineResyncTime != null &&
                                       NearlyEqual(time, nextBarlineResyncTime.Value);

                while (timingPointIndex < timingPoints.Count && timingPoints[timingPointIndex].StartTime == time)
                {
                    var timingPoint = timingPoints[timingPointIndex++];
                    currentBpm = timingPoint.Bpm;
                    currentOutputBpm = currentBpm;
                    currentTimingPointStart = timingPoint.StartTime;
                    currentMeter = (int) timingPoint.Signature;
                    output.Add(new TimingPoint(timingPoint));
                    hasOutputTimingPoint = true;
                    sourceTimingPointChanged = true;
                }

                // A source timing point already restores the correct barline phase.
                if (sourceTimingPointChanged)
                    nextBarlineResyncTime = null;

                while (sliderVelocityIndex < sliderVelocities.Count && sliderVelocities[sliderVelocityIndex].StartTime == time)
                {
                    var sliderVelocity = sliderVelocities[sliderVelocityIndex++];
                    currentSliderVelocity = sliderVelocity.Multiplier;
                    hasSliderVelocity = true;
                }

                if (!hasSliderVelocity)
                    continue;

                // Keep BPM * SV unchanged while constraining osu!'s inherited multiplier.
                var (targetBpm, targetSliderVelocity) = GetOutputTiming(currentBpm, currentSliderVelocity);

                if (!hasOutputTimingPoint || !NearlyEqual(currentOutputBpm, targetBpm))
                {
                    var isMeasureStart = IsMeasureStart(time, currentTimingPointStart, currentBpm, currentMeter);
                    var requiresBpmCompensation = !NearlyEqual(targetBpm, currentBpm);
                    var meter = isMeasureStart && !requiresBpmCompensation
                        ? currentMeter
                        : largeMeter;

                    // Suppress shifted barlines; a point on a measure start still displays its first barline.
                    output.Add(TimingPoint.TimingChange((float) time, targetBpm, meter, !isMeasureStart));
                    currentOutputBpm = targetBpm;
                    hasOutputTimingPoint = true;

                    if (!isMeasureStart || requiresBpmCompensation)
                    {
                        nextBarlineResyncTime = GetNextMeasureStart(
                            time, currentTimingPointStart, currentBpm, currentMeter, barlineResyncEndTime);
                    }
                    else if (barlineResyncDue)
                    {
                        // This aligned timing change also fulfils the pending resync.
                        nextBarlineResyncTime = null;
                    }
                }

                if (barlineResyncDue && nextBarlineResyncTime != null &&
                    NearlyEqual(time, nextBarlineResyncTime.Value))
                {
                    var requiresBpmCompensation = !NearlyEqual(currentOutputBpm, currentBpm);
                    var meter = requiresBpmCompensation ? largeMeter : currentMeter;

                    // Keep emitting only source-aligned barlines while the compensated BPM remains active.
                    output.Add(TimingPoint.TimingChange((float) time, currentOutputBpm, meter, false));
                    nextBarlineResyncTime = requiresBpmCompensation
                        ? GetNextMeasureStart(
                            time, currentTimingPointStart, currentBpm, currentMeter, barlineResyncEndTime)
                        : null;
                }

                output.Add(TimingPoint.SliderVelocity((float) time, targetSliderVelocity));
            }

            return output;
        }

        private static (double Bpm, double SliderVelocity) GetOutputTiming(float bpm, float sliderVelocity)
        {
            if (bpm <= 0 || sliderVelocity <= 0)
                return (bpm, MinOsuSliderVelocity);

            // Compensate for clamping by moving the remaining scale into the BPM.
            if (sliderVelocity < MinOsuSliderVelocity)
                return (bpm * sliderVelocity / MinOsuSliderVelocity, MinOsuSliderVelocity);

            if (sliderVelocity > MaxOsuSliderVelocity)
                return (bpm * sliderVelocity / MaxOsuSliderVelocity, MaxOsuSliderVelocity);

            return (bpm, sliderVelocity);
        }

        private static bool IsMeasureStart(double time, double timingPointStart, double bpm, int meter)
        {
            if (bpm <= 0 || meter <= 0)
                return false;

            var measureLength = 60000 / bpm * meter;
            var measuresFromStart = (time - timingPointStart) / measureLength;
            return NearlyEqual(measuresFromStart, Math.Round(measuresFromStart));
        }

        private static double? GetNextMeasureStart(
            double time, double timingPointStart, double bpm, int meter, double endTime)
        {
            var measureLength = 60000 / bpm * meter;
            var measuresFromStart = (time - timingPointStart) / measureLength;
            var nextMeasureStart = timingPointStart + (Math.Floor(measuresFromStart) + 1) * measureLength;
            return nextMeasureStart <= endTime ? Math.Round(nextMeasureStart) : null;
        }

        private static bool NearlyEqual(double x, double y) => Math.Abs(x - y) < FloatTolerance;
    }
}
