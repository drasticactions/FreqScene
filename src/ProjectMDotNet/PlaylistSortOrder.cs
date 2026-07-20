using ProjectMDotNet.Interop;

namespace ProjectMDotNet;

/// <summary>Direction used when sorting playlist items.</summary>
public enum PlaylistSortOrder
{
    /// <summary>Ascending order.</summary>
    Ascending = (int)projectm_playlist_sort_order.SORT_ORDER_ASCENDING,

    /// <summary>Descending order.</summary>
    Descending = (int)projectm_playlist_sort_order.SORT_ORDER_DESCENDING,
}
