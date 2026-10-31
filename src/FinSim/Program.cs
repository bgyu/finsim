using FinSim.Infrastructure.Cache;
using FinSim.Infrastructure.Slurm;
using FinSimApp.Batch;

if (args.Length != 1 || args[0] is not ("master" or "worker"))
{
    Console.Error.WriteLine("usage: FinSim master|worker");
    return 64;
}

try
{
    var master = args[0] == "master";
    var settings = BatchSettings.FromEnvironment(requireSlurm: master);
    if (settings.ServiceCaFile is not null)
    {
        // .NET on Linux asks OpenSSL for its trust store. Propagate the
        // deployment-projected bundle before the first HTTPS connection.
        Environment.SetEnvironmentVariable("SSL_CERT_FILE", settings.ServiceCaFile);
    }
    using var cacheHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    var cache = new HttpKeyValueStore(
        cacheHttp,
        settings.CacheUrl,
        settings.CacheTokenFile,
        settings.CacheAllowInsecureHttp);

    if (master)
    {
        using var slurmHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var slurm = new SlurmRestClient(
            slurmHttp,
            settings.SlurmUrl!,
            settings.SlurmTokenFile!,
            "finsim",
            settings.SlurmAllowInsecureHttp);
        return await new BatchRunner(settings, cache, slurm)
            .RunMasterAsync(CancellationToken.None);
    }

    var taskId = Environment.GetEnvironmentVariable("SLURM_ARRAY_TASK_ID");
    if (!int.TryParse(taskId, out var chunkIndex))
    {
        throw new InvalidOperationException("SLURM_ARRAY_TASK_ID is invalid.");
    }

    return await new BatchRunner(settings, cache)
        .RunWorkerAsync(chunkIndex, CancellationToken.None);
}
catch (Exception error)
{
    Console.Error.WriteLine(
        System.Text.Json.JsonSerializer.Serialize(new
        {
            event_name = "startup_failed",
            error = error.GetType().Name
        }));
    return 78;
}
