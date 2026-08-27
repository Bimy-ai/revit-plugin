namespace BimyRevit.Models;

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

    /// <summary>
    /// The project's Revit model: the faithful IFC (walls, floors, ceilings,
    /// doors, windows, openings, spaces, materials, property sets), served on
    /// demand for ANY project — the freshest of a published snapshot, the
    /// project's stored model, or a server-side generation from its drawings.
    /// Pulled by the plugin and opened natively.
    /// </summary>
    public static string RevitIfcUrl(BimyEnvironment env, string projectId)
        => $"{BaseUrl(env)}/api/export/revit-ifc/{projectId}";

    /// <summary>
    /// The workspace's projects, newest first, through the generic CRUD list
    /// every deployment serves. Used to fill the picker with real names instead
    /// of asking the user to paste a 24-character id.
    /// </summary>
    public static string ProjectsUrl(BimyEnvironment env, int limit = 200)
        => $"{BaseUrl(env)}/api/data?model=Project&sort=-_id&limit={limit}";

    /// <summary>The project's page in the BIMy web app — for "Open in BIMy".</summary>
    public static string ProjectWebUrl(BimyEnvironment env, string projectId)
        => $"{BaseUrl(env)}/projects/{projectId}";
}
