using System;
using System.Collections.Generic;
using System.Linq;

namespace Parsec.App;

/// <summary>
/// How a single parameter interpolates between keyframes.
/// </summary>
public enum InterpKind
{
    /// <summary>Plain linear interpolation (the default for scalar params).</summary>
    Linear,
    /// <summary>
    /// Angular value that wraps at 2*pi: interpolate the SHORTEST way around the
    /// circle, so e.g. 6.0 -> 0.2 sweeps forward across the wrap rather than
    /// backward through the whole range. For radian-valued params (camera
    /// orientation in phase 2).
    /// </summary>
    AngularWrap,
    /// <summary>
    /// Cyclic value with period 1.0 (the palette phases: the cosine palette is
    /// periodic in phase with period 1). Same shortest-way rule as
    /// <see cref="AngularWrap"/>, so 0.95 -> 0.05 sweeps forward across the
    /// wrap instead of backward through the whole palette.
    /// </summary>
    UnitWrap,
}

/// <summary>
/// One keyframe: a full snapshot of every parameter value (not a diff), aligned
/// positionally with the timeline's descriptor list. Full snapshots make
/// interpolation a trivial per-index lerp between two keyframes and avoid having
/// to reconstruct "what was slider X at frame N" by searching backward.
/// </summary>
public sealed class Keyframe
{
    public bool IsSet;
    public double[] Values = Array.Empty<double>();

    public Keyframe Clone() => new() { IsSet = IsSet, Values = (double[])Values.Clone() };
}

/// <summary>
/// The animation timeline: a fixed bank of keyframe slots over which parameter
/// values are interpolated during playback. Pure logic -- no UI, no rendering.
///
/// Model (the agreed MVP):
///   - Fixed number of slots, each representing a fixed segment duration.
///   - A slot becomes "set" when the user first moves a slider while it is
///     selected (the bank calls <see cref="CaptureInto"/>); selection alone does
///     not set it. Slot 0 is auto-set on construction and cannot be cleared.
///   - Empty slots are just time: playback interpolates between consecutive SET
///     keyframes, spanning any empty gap between them.
///   - Playback stops at the last set keyframe (no loop).
///
/// The timeline is bound to a specific ordered list of parameter descriptors
/// (the active fractal's schema + palette), so capture/apply just walk the
/// descriptors' Get/Set closures -- the same ones the panel uses.
/// </summary>
public sealed class Timeline
{
    public const int SlotCount = 30;
    public const double SecondsPerSegment = 2.0;

    private readonly Keyframe[] _slots = new Keyframe[SlotCount];
    private readonly IReadOnlyList<ParamDescriptor> _descriptors;
    private readonly InterpKind[] _kinds;

    public int SelectedIndex { get; private set; }

    /// <summary>
    /// Identifies which fractal+palette schema this timeline's Values arrays are
    /// aligned to. A saved timeline can only be loaded back onto a matching
    /// schema (same fractal), since keyframe values align to descriptors by
    /// position. Set by the host when building/loading.
    /// </summary>
    public string SchemaTag { get; set; } = "";

    /// <summary>
    /// Build a timeline bound to the given descriptors. <paramref name="kindFor"/>
    /// maps a descriptor to its interpolation kind (defaults to Linear).
    /// Slot 0 is captured immediately from current state so it always has a value.
    /// </summary>
    public Timeline(IReadOnlyList<ParamDescriptor> descriptors,
        Func<ParamDescriptor, InterpKind>? kindFor = null)
    {
        _descriptors = descriptors;
        _kinds = descriptors.Select(d => kindFor?.Invoke(d) ?? InterpKind.Linear).ToArray();
        for (int i = 0; i < SlotCount; i++)
            _slots[i] = new Keyframe { IsSet = false, Values = new double[descriptors.Count] };

        SelectedIndex = 0;
        CaptureInto(0);   // slot 0 always holds a value; cannot be cleared
    }

    public bool IsSet(int index) => _slots[index].IsSet;
    public int Count => _slots.Count(s => s.IsSet);

    public void Select(int index)
    {
        if (index < 0 || index >= SlotCount) return;
        SelectedIndex = index;
    }

    /// <summary>Snapshot the current live parameter values into a slot and set it.</summary>
    public void CaptureInto(int index)
    {
        if (index < 0 || index >= SlotCount) return;
        var vals = new double[_descriptors.Count];
        for (int i = 0; i < _descriptors.Count; i++)
            vals[i] = _descriptors[i].Get();
        _slots[index].Values = vals;
        _slots[index].IsSet = true;
    }

    /// <summary>Clear a slot (slot 0 is protected and never clears).</summary>
    public bool Clear(int index)
    {
        if (index <= 0 || index >= SlotCount) return false;
        if (!_slots[index].IsSet) return false;
        _slots[index].IsSet = false;
        return true;
    }

    /// <summary>The largest index that is set (the playback end). >= 0 always.</summary>
    public int LastSetIndex()
    {
        for (int i = SlotCount - 1; i >= 0; i--)
            if (_slots[i].IsSet) return i;
        return 0;
    }

    /// <summary>
    /// Total playback duration in seconds, measured from <paramref name="fromIndex"/>
    /// to the last set keyframe.
    /// </summary>
    public double DurationFrom(int fromIndex)
    {
        int last = LastSetIndex();
        return Math.Max(0, (last - fromIndex)) * SecondsPerSegment;
    }

    /// <summary>
    /// Compute the interpolated parameter values at playback time <paramref name="t"/>
    /// seconds after starting from <paramref name="fromIndex"/>, and write them
    /// back through the descriptors' setters. Returns false when playback is
    /// complete (t has reached/passed the last set keyframe).
    /// </summary>
    public bool ApplyAtTime(int fromIndex, double t)
    {
        int last = LastSetIndex();
        if (last <= fromIndex)
        {
            ApplyValues(_slots[NearestSetAtOrBefore(fromIndex)].Values);
            return false; // nothing after the start; immediately done
        }

        // Absolute slot position (fractional) at this time.
        double slotPos = fromIndex + t / SecondsPerSegment;
        if (slotPos >= last)
        {
            ApplyValues(_slots[last].Values);
            return false; // reached the end
        }

        // Find the set keyframes bracketing slotPos: the nearest set slot at or
        // before it, and the nearest set slot after it. The backward search runs
        // past fromIndex all the way to slot 0 (always set) -- empty slots are
        // just time, so starting playback from an unset slot mid-gap must
        // interpolate the surrounding SET keyframes, never lerp from the unset
        // slot's zero-filled snapshot.
        int lo = NearestSetAtOrBefore((int)Math.Floor(slotPos));
        int hi = lo;
        for (int i = lo + 1; i <= last; i++)
            if (_slots[i].IsSet) { hi = i; break; }

        if (hi == lo)
        {
            ApplyValues(_slots[lo].Values);
            return true;
        }

        double frac = (slotPos - lo) / (hi - lo);
        var outv = new double[_descriptors.Count];
        var a = _slots[lo].Values;
        var b = _slots[hi].Values;
        for (int i = 0; i < outv.Length; i++)
            outv[i] = Interp(a[i], b[i], frac, _kinds[i]);
        ApplyValues(outv);
        return true;
    }

    /// <summary>The nearest set slot at or before <paramref name="index"/>.
    /// Falls through to slot 0, which is always set.</summary>
    private int NearestSetAtOrBefore(int index)
    {
        for (int i = Math.Min(index, SlotCount - 1); i > 0; i--)
            if (_slots[i].IsSet) return i;
        return 0;
    }

    /// <summary>Apply a set of values back to the live state via the setters.</summary>
    public void ApplySlot(int index) => ApplyValues(_slots[index].Values);

    private void ApplyValues(double[] vals)
    {
        for (int i = 0; i < _descriptors.Count && i < vals.Length; i++)
            _descriptors[i].Set(vals[i]);
    }

    private static double Interp(double a, double b, double t, InterpKind kind)
    {
        if (kind == InterpKind.AngularWrap) return WrapLerp(a, b, t, Math.PI * 2.0);
        if (kind == InterpKind.UnitWrap) return WrapLerp(a, b, t, 1.0);
        return a + (b - a) * t;
    }

    /// <summary>Lerp on a circle of circumference <paramref name="period"/>,
    /// taking the shortest way around; result normalized to [0, period).</summary>
    private static double WrapLerp(double a, double b, double t, double period)
    {
        double half = period * 0.5;
        double d = (b - a) % period;
        if (d > half) d -= period;
        if (d < -half) d += period;
        double v = (a + d * t) % period;
        if (v < 0) v += period;
        return v;
    }

    // ----------------------------------------------------------- persistence
    private sealed class TimelineDto
    {
        public string SchemaTag { get; set; } = "";
        public int DescriptorCount { get; set; }
        public int SelectedIndex { get; set; }
        public List<SlotDto> Slots { get; set; } = new();
    }

    private sealed class SlotDto
    {
        public bool IsSet { get; set; }
        public double[] Values { get; set; } = Array.Empty<double>();
    }

    /// <summary>Serialize the timeline to JSON (keyframes, set flags, tag).</summary>
    public string ToJson()
    {
        var dto = new TimelineDto
        {
            SchemaTag = SchemaTag,
            DescriptorCount = _descriptors.Count,
            SelectedIndex = SelectedIndex,
            Slots = _slots.Select(s => new SlotDto
            {
                IsSet = s.IsSet,
                Values = (double[])s.Values.Clone(),
            }).ToList(),
        };
        return System.Text.Json.JsonSerializer.Serialize(dto,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Load keyframe data from JSON into this timeline. Returns false (and leaves
    /// the timeline unchanged) if the data doesn't match this timeline's schema
    /// (different fractal or descriptor count), since values align by position.
    /// </summary>
    public bool LoadJson(string json)
    {
        TimelineDto? dto;
        try { dto = System.Text.Json.JsonSerializer.Deserialize<TimelineDto>(json); }
        catch { return false; }
        if (dto is null) return false;
        if (dto.DescriptorCount != _descriptors.Count) return false;
        if (!string.IsNullOrEmpty(SchemaTag) && dto.SchemaTag != SchemaTag) return false;

        for (int i = 0; i < SlotCount && i < dto.Slots.Count; i++)
        {
            var s = dto.Slots[i];
            _slots[i].IsSet = s.IsSet;
            _slots[i].Values = s.Values.Length == _descriptors.Count
                ? (double[])s.Values.Clone()
                : new double[_descriptors.Count];
        }
        _slots[0].IsSet = true;  // slot 0 always set
        SelectedIndex = Math.Clamp(dto.SelectedIndex, 0, SlotCount - 1);
        return true;
    }
}
