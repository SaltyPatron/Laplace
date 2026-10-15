using Laplace.Cli;

return await ApiClientCommands.RunAsync(
    args.Length > 0 && args[0] == "--api-client" ? args[1..] : args);
