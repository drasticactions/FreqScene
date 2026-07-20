namespace ProjectMDotNet;

/// <summary>Thrown when a projectM native operation fails.</summary>
public class ProjectMException : Exception
{
    /// <summary>Initializes a new instance with a message.</summary>
    public ProjectMException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance with a message and inner exception.</summary>
    public ProjectMException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
