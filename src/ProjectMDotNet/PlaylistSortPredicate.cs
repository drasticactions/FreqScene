using ProjectMDotNet.Interop;

namespace ProjectMDotNet;

/// <summary>Key used when sorting playlist items.</summary>
public enum PlaylistSortPredicate
{
    /// <summary>Sort by the full preset path.</summary>
    FullPath = (int)projectm_playlist_sort_predicate.SORT_PREDICATE_FULL_PATH,

    /// <summary>Sort by file name only.</summary>
    FilenameOnly = (int)projectm_playlist_sort_predicate.SORT_PREDICATE_FILENAME_ONLY,
}
