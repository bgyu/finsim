using FinSim.Contracts;

namespace FinSimApp.Batch;

public static class ChunkCalculator
{
    public static ChunkResult Calculate(ChunkData chunk)
    {
        if (chunk.Values.Count == 0)
        {
            throw new ArgumentException("A chunk must contain at least one value.", nameof(chunk));
        }

        long sum = 0;
        var minimum = long.MaxValue;
        var maximum = long.MinValue;
        checked
        {
            foreach (var value in chunk.Values)
            {
                sum += value;
                minimum = Math.Min(minimum, value);
                maximum = Math.Max(maximum, value);
            }
        }

        return new ChunkResult(
            chunk.WorkflowId,
            chunk.ChunkIndex,
            chunk.Values.Count,
            sum,
            minimum,
            maximum);
    }
}
