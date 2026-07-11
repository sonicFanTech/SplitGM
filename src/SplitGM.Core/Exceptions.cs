namespace SplitGM.Core;

public class SplitGmException : Exception
{
    public SplitGmException(string message) : base(message) { }
    public SplitGmException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class UnsupportedInputException : SplitGmException
{
    public UnsupportedInputException(string message) : base(message) { }
}

public sealed class YycGameException : SplitGmException
{
    public YycGameException(string message) : base(message) { }
}
