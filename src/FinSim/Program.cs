if (args.Length != 1 || args[0] is not ("master" or "worker"))
{
    Console.Error.WriteLine("usage: FinSim master|worker");
    return 64;
}

Console.Error.WriteLine($"FinSim {args[0]} is not configured");
return 78;
