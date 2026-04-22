namespace RevitWallsPlugin.Models;

public enum BimyEnvironment
{
    Production = 0,
    Sandbox = 1,
    Staging = 2,
    Demo = 3,
}

public static class BimyEnvironments
{
    public static readonly IReadOnlyList<BimyEnvironment> All = new[]
    {
        BimyEnvironment.Production,
        BimyEnvironment.Sandbox,
        BimyEnvironment.Staging,
        BimyEnvironment.Demo,
    };

    public const BimyEnvironment Default = BimyEnvironment.Production;

    public static string BaseUrl(BimyEnvironment env) => env switch
    {
        BimyEnvironment.Production => "https://bimy.app",
        BimyEnvironment.Sandbox    => "https://sandbox.bimy.dev",
        BimyEnvironment.Staging    => "https://staging.bimy.dev",
        BimyEnvironment.Demo       => "https://demo.bimy.app",
        _ => throw new ArgumentOutOfRangeException(nameof(env), env, null),
    };

    public static string DisplayName(BimyEnvironment env) => env switch
    {
        BimyEnvironment.Production => "Production",
        BimyEnvironment.Sandbox    => "Sandbox",
        BimyEnvironment.Staging    => "Staging",
        BimyEnvironment.Demo       => "Demo",
        _ => env.ToString(),
    };

    public static string AuthUrl(BimyEnvironment env) => BaseUrl(env) + "/api/auth";

    public static string ProjectDataUrl(BimyEnvironment env, string projectId)
        => $"{BaseUrl(env)}/api/data/{projectId}?model=Project";
}
