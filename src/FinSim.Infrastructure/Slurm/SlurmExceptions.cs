namespace FinSim.Infrastructure.Slurm;

public abstract class SlurmException(string message, Exception? inner = null)
    : Exception(message, inner)
{
}

public sealed class SlurmRejectedException(string message)
    : SlurmException(message)
{
}

public sealed class SlurmAmbiguousException(string message, Exception? inner = null)
    : SlurmException(message, inner)
{
}

public sealed class SlurmUnavailableException(string message, Exception? inner = null)
    : SlurmException(message, inner)
{
}
