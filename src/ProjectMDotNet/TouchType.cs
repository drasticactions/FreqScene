using ProjectMDotNet.Interop;

namespace ProjectMDotNet;

/// <summary>Waveform type spawned by touch interactions.</summary>
public enum TouchType
{
    /// <summary>A randomly selected waveform type.</summary>
    Random = (int)projectm_touch_type.PROJECTM_TOUCH_TYPE_RANDOM,

    /// <summary>Circle waveform.</summary>
    Circle = (int)projectm_touch_type.PROJECTM_TOUCH_TYPE_CIRCLE,

    /// <summary>Radial blob waveform.</summary>
    RadialBlob = (int)projectm_touch_type.PROJECTM_TOUCH_TYPE_RADIAL_BLOB,

    /// <summary>Blob (style 2) waveform.</summary>
    Blob2 = (int)projectm_touch_type.PROJECTM_TOUCH_TYPE_BLOB2,

    /// <summary>Blob (style 3) waveform.</summary>
    Blob3 = (int)projectm_touch_type.PROJECTM_TOUCH_TYPE_BLOB3,

    /// <summary>Derivative line waveform.</summary>
    DerivativeLine = (int)projectm_touch_type.PROJECTM_TOUCH_TYPE_DERIVATIVE_LINE,

    /// <summary>Blob (style 5) waveform.</summary>
    Blob5 = (int)projectm_touch_type.PROJECTM_TOUCH_TYPE_BLOB5,

    /// <summary>Line waveform.</summary>
    Line = (int)projectm_touch_type.PROJECTM_TOUCH_TYPE_LINE,

    /// <summary>Double line waveform.</summary>
    DoubleLine = (int)projectm_touch_type.PROJECTM_TOUCH_TYPE_DOUBLE_LINE,
}
