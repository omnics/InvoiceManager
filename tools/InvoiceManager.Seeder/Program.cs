using System.Globalization;
using System.Text.Json;
using InvoiceManager.Core;
using InvoiceManager.Infrastructure.CosmosDb;
using InvoiceManager.Seeder;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using NodaMoney;

// Cosmos connection details come from configuration so the seeder shares a single
// client-creation path with the running app (CosmosClientFactory): locally the Aspire
// emulator supplies ConnectionStrings:cosmos (key-based); in the cloud the deploy script
// supplies CosmosEndpoint and the seeder authenticates with DefaultAzureCredential.
var configuration = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .Build();

// Flags:
//   --ensure-schema      create the database and containers before seeding. Passed only by the
//                        local Aspire bootstrap: the emulator starts empty. In the cloud
//                        Terraform owns schema and the seeder's identity holds only a data role.
//   --clear-database     delete every item from every container (data-plane deletes) before
//                        seeding, giving a clean slate. Refused against production without --force.
//   --clear-records-only delete every item from invoice-records and freeagent-interventions only
//                        (data-plane deletes), then exit without touching invoice-configurations
//                        or loading the seed file at all. For winding a Test environment's run
//                        history back to empty between manual test cycles without disturbing
//                        configurations edited by hand since they were seeded. Mutually exclusive
//                        with --clear-database; refused against production without --force.
//   --force              override the production --clear-database/--clear-records-only guard.
//   --environment <n>    deployment environment (required). When "test", downloads are nested
//                        under a root "Test" folder so they never collide with production files.
var (ensureSchema, clearDatabase, clearRecordsOnly, force, environment, positionalArgs) = ParseArgs(args);

if (string.IsNullOrWhiteSpace(environment))
{
    await Console.Error.WriteLineAsync(
        "The --environment option is required, e.g. --environment test or --environment production.");
    Console.Out.Flush();
    Console.Error.Flush();
    Environment.Exit(5);
}

if (clearDatabase && clearRecordsOnly)
{
    await Console.Error.WriteLineAsync(
        "--clear-database and --clear-records-only are mutually exclusive.");
    Console.Out.Flush();
    Console.Error.Flush();
    Environment.Exit(6);
}

var isTest = string.Equals(environment, "test", StringComparison.OrdinalIgnoreCase);
var isProduction = string.Equals(environment, "production", StringComparison.OrdinalIgnoreCase);

if (clearDatabase && isProduction && !force)
{
    await Console.Error.WriteLineAsync(
        "Refusing --clear-database against the production environment. Pass --force to override.");
    Console.Out.Flush();
    Console.Error.Flush();
    Environment.Exit(4);
}

if (clearRecordsOnly && isProduction && !force)
{
    await Console.Error.WriteLineAsync(
        "Refusing --clear-records-only against the production environment. Pass --force to override.");
    Console.Out.Flush();
    Console.Error.Flush();
    Environment.Exit(4);
}

var databaseName = configuration["CosmosDatabase"] ?? "invoicemanager";

if (clearRecordsOnly)
{
    Console.WriteLine("Seeder starting.");
    Console.WriteLine($"  Database:    {databaseName}");
    Console.WriteLine($"  Environment: {environment}");
    Console.WriteLine("  Mode:        Clear records only (invoice-records, freeagent-interventions) - invoice-configurations untouched");

    var recordsOnlyClient = CosmosClientFactory.Create(configuration);
    await ClearContainersAsync(
        recordsOnlyClient, databaseName, [CosmosSchema.InvoiceRecords, CosmosSchema.FreeAgentInterventions]);
    Console.WriteLine("Clear complete.");
    Console.Out.Flush();
    return;
}

var seedFilePath = positionalArgs.Length > 0
    ? positionalArgs[0]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "seed", "invoice-configurations.json");

Console.WriteLine($"Seeder starting.");
Console.WriteLine($"  Database:       {databaseName}");
Console.WriteLine($"  Environment:    {environment}");
Console.WriteLine($"  Ensure schema:  {ensureSchema}");
Console.WriteLine($"  Clear database: {clearDatabase}");
Console.WriteLine($"  Seed file:      {Path.GetFullPath(seedFilePath)}");

var json = await File.ReadAllTextAsync(seedFilePath);

// The seed file ships readable placeholders for values we must not commit (the
// OneDrive drive id lives inside a path, the M365 billing account id is a whole
// field). Substitute the real values from configuration before deserializing so a
// single string replace covers both the embedded and standalone cases.
json = ReplaceSeedTokens(json, configuration, isTest);

var records = JsonSerializer.Deserialize<List<SeedInvoiceConfigurationRecord>>(
    json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
    ?? throw new InvalidOperationException("Seed file is empty or invalid.");

Console.WriteLine($"Loaded {records.Count} configuration(s) from seed file.");

var configurations = records.Select(r => new InvoiceConfiguration(
    new InvoiceConfigurationId(r.Id),
    ToIntegrationConfiguration(r),
    r.InvoiceDescription,
    Enum.Parse<InvoiceFrequency>(r.Frequency, ignoreCase: true),
    r.AmountMatchingCriteria is { } amountCriteria
        ? new AmountMatchingCriteria(new Money(amountCriteria.Amount, amountCriteria.Currency), amountCriteria.AmountTolerance)
        : Option.None,
    Enum.Parse<VatMode>(r.DefaultVatMode, ignoreCase: true),
    r.IsActive,
    ToOneDriveFolder(r.OneDriveFolder, isTest),
    DateOnly.ParseExact(r.StartDate, "O", CultureInfo.InvariantCulture),
    r.DateToleranceDays,
    // Seed files do not yet support FreeAgent bill matching.
    Option.None)).ToList();

var cosmosClient = CosmosClientFactory.Create(configuration);

try
{
    if (ensureSchema)
    {
        await EnsureSchemaAsync(cosmosClient, databaseName);
    }

    if (clearDatabase)
    {
        await ClearContainersAsync(cosmosClient, databaseName, CosmosSchema.Containers);
    }

    var repository = new CosmosInvoiceConfigurationRepository(cosmosClient, databaseName);
    var seeder = new ConfigurationSeeder(repository);
    await seeder.SeedAsync(configurations);
}
catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
{
    await Console.Error.WriteLineAsync(
        $"Cosmos DB returned 403 Forbidden — the RBAC role assignment may not yet have " +
        $"propagated. ({ex.Message})");
    Console.Out.Flush();
    Console.Error.Flush();
    Environment.Exit(2);
}

Console.WriteLine("Seeding complete.");

static string ReplaceSeedTokens(string json, IConfiguration configuration, bool isTest)
{
    // token in seed file  ->  config key (env var: InvoiceManager__Seed__<name>)
    //
    // The two folder-item-id tokens resolve to different config keys depending on
    // environment: production configurations must address the real production
    // "/Bills/<name>" folders, while test configurations must address a separate,
    // isolated "/Test/Bills/<name>" folder tree in the same drive (a real, distinct
    // Graph item, not a cosmetic path prefix) so test runs never read or write the
    // production files that live at a different, unrelated item id.
    var tokens = new (string Token, string ConfigKey, string EnvVar)[]
    {
        ("REPLACE_WITH_DRIVE_ID", "InvoiceManager:Seed:DriveId", "InvoiceManager__Seed__DriveId"),
        ("REPLACE_WITH_DRIVE_NAME", "InvoiceManager:Seed:DriveName", "InvoiceManager__Seed__DriveName"),
        ("REPLACE_WITH_MICROSOFT365_FOLDER_ITEM_ID",
            isTest ? "InvoiceManager:Seed:Microsoft365TestFolderItemId" : "InvoiceManager:Seed:Microsoft365FolderItemId",
            isTest ? "InvoiceManager__Seed__Microsoft365TestFolderItemId" : "InvoiceManager__Seed__Microsoft365FolderItemId"),
        ("REPLACE_WITH_AZURE_FOLDER_ITEM_ID",
            isTest ? "InvoiceManager:Seed:AzureTestFolderItemId" : "InvoiceManager:Seed:AzureFolderItemId",
            isTest ? "InvoiceManager__Seed__AzureTestFolderItemId" : "InvoiceManager__Seed__AzureFolderItemId"),
        ("REPLACE_WITH_BILLING_ACCOUNT_ID", "InvoiceManager:Seed:BillingAccountId", "InvoiceManager__Seed__BillingAccountId"),
        ("REPLACE_WITH_AZURE_BILLING_ACCOUNT_ID", "InvoiceManager:Seed:AzureBillingAccountId", "InvoiceManager__Seed__AzureBillingAccountId"),
    };

    var required = new List<(string EnvVar, bool IsSet)>();
    var missing = new List<string>();
    foreach (var (token, configKey, envVar) in tokens)
    {
        // Only require a value when its token is actually present, so seed files that
        // don't use a token (e.g. future non-M365 configs) need no extra configuration.
        if (!json.Contains(token, StringComparison.Ordinal))
        {
            continue;
        }

        var value = configuration[configKey];
        var isSet = !string.IsNullOrWhiteSpace(value);
        required.Add((envVar, isSet));
        if (!isSet)
        {
            missing.Add(envVar);
            continue;
        }

        json = json.Replace(token, value!, StringComparison.Ordinal);
    }

    if (missing.Count > 0)
    {
        // List every variable this seed file (for this --environment) needs, not just the
        // missing ones, so a partially-configured environment doesn't leave the operator
        // guessing at the full picture — and flush explicitly before Environment.Exit,
        // since an abrupt process exit can otherwise truncate buffered console output when
        // this process is launched by a host (e.g. the Aspire AppHost) rather than a
        // directly-attached terminal.
        Console.Error.WriteLine(
            $"Seed file requires {required.Count} environment variable(s) for --environment " +
            $"{(isTest ? "test" : "production")}; {missing.Count} of them are not configured:");
        foreach (var (envVar, isSet) in required)
        {
            Console.Error.WriteLine($"  [{(isSet ? "OK" : "MISSING")}] {envVar}");
        }
        Console.Error.WriteLine(
            "Run tools/dev-setup/Set-SeedEnvironment.ps1 to discover and set the required values.");
        Console.Out.Flush();
        Console.Error.Flush();
        Environment.Exit(3);
    }

    return json;
}

static async Task EnsureSchemaAsync(CosmosClient client, string databaseName)
{
    var database = await client.CreateDatabaseIfNotExistsAsync(databaseName);
    Console.WriteLine($"Ensured database '{databaseName}'.");

    foreach (var container in CosmosSchema.Containers)
    {
        await database.Database.CreateContainerIfNotExistsAsync(
            new ContainerProperties(container.Name, container.PartitionKeyPath));
        Console.WriteLine($"Ensured container '{container.Name}' ({container.PartitionKeyPath}).");
    }
}

// Deletes every item from each given container using data-plane deletes only, so it works
// with just the seeder's Cosmos data-plane role (no control-plane container recreate).
static async Task ClearContainersAsync(
    CosmosClient client, string databaseName, IReadOnlyList<ContainerDefinition> containers)
{
    foreach (var definition in containers)
    {
        var container = client.GetContainer(databaseName, definition.Name);
        var partitionKeyProperty = definition.PartitionKeyPath.TrimStart('/');

        // Read all ids + partition-key values first, then delete, so we never mutate a
        // container while its query iterator is still open.
        var toDelete = new List<(string Id, string PartitionKey)>();
        using var iterator = container.GetItemQueryIterator<JsonElement>("SELECT * FROM c");
        while (iterator.HasMoreResults)
        {
            foreach (var item in await iterator.ReadNextAsync())
            {
                var id = item.GetProperty("id").GetString();
                var partitionKey = item.GetProperty(partitionKeyProperty).GetString();
                if (id is not null && partitionKey is not null)
                {
                    toDelete.Add((id, partitionKey));
                }
            }
        }

        foreach (var (id, partitionKey) in toDelete)
        {
            await container.DeleteItemAsync<JsonElement>(id, new PartitionKey(partitionKey));
        }

        Console.WriteLine($"Cleared {toDelete.Count} item(s) from '{definition.Name}'.");
    }
}

static IntegrationConfiguration ToIntegrationConfiguration(SeedInvoiceConfigurationRecord record) =>
    Enum.Parse<IntegrationType>(record.IntegrationType, ignoreCase: true) switch
    {
        IntegrationType.GraphEmail => new GraphEmailIntegrationConfiguration(record.SenderEmailAddress, record.BodyPattern),
        IntegrationType.MicrosoftBilling => new MicrosoftBillingIntegrationConfiguration(record.BillingAccountId),
        var other => throw new InvalidOperationException($"Unrecognised integration type '{other}' in seed file."),
    };

// driveId comes from its own (placeholder-token) field; folderItemId was already resolved
// to the environment-correct real item (production vs. the separate Test/Bills/<name> tree)
// by ReplaceSeedTokens above. Only the display-only folderPath needs an explicit "/Test"
// prefix here, purely so an operator reading the stored configuration can see which
// environment it belongs to — it plays no part in addressing the folder on Graph.
static OneDriveFolder ToOneDriveFolder(SeedOneDriveFolder folder, bool isTest) =>
    new(folder.DriveId, folder.DriveName, folder.FolderItemId, isTest ? $"/Test{folder.FolderPath}" : folder.FolderPath);

static (bool EnsureSchema, bool ClearDatabase, bool ClearRecordsOnly, bool Force, string? Environment, string[] Positional)
    ParseArgs(string[] args)
{
    var ensureSchema = false;
    var clearDatabase = false;
    var clearRecordsOnly = false;
    var force = false;
    string? environment = null;
    var positional = new List<string>();

    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (arg.Equals("--ensure-schema", StringComparison.OrdinalIgnoreCase))
        {
            ensureSchema = true;
        }
        else if (arg.Equals("--clear-database", StringComparison.OrdinalIgnoreCase))
        {
            clearDatabase = true;
        }
        else if (arg.Equals("--clear-records-only", StringComparison.OrdinalIgnoreCase))
        {
            clearRecordsOnly = true;
        }
        else if (arg.Equals("--force", StringComparison.OrdinalIgnoreCase))
        {
            force = true;
        }
        else if (arg.Equals("--environment", StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 >= args.Length)
            {
                throw new ArgumentException("--environment requires a value, e.g. --environment test.");
            }

            environment = args[++i];
        }
        else if (arg.Equals("--", StringComparison.Ordinal))
        {
            // AppHost's WithArgs leads with a literal "--" because Aspire launches project
            // resources two different ways depending on how the AppHost itself was started:
            // via `dotnet run`, which (on this repo's .NET 11 preview SDK) only forwards
            // trailing args to the child app when they follow a "--"; or, under a debugger
            // (e.g. Visual Studio), by exec'ing the built binary directly with WithArgs's raw
            // list, bypassing `dotnet run` entirely. Only the first path consumes the "--" --
            // the second passes it straight through as a literal token. Recognise and drop it
            // here rather than erroring, the same way dotnet/git/npm treat a bare "--" as a
            // separator rather than option data.
        }
        else if (arg.StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unknown option '{arg}'.");
        }
        else
        {
            positional.Add(arg);
        }
    }

    return (ensureSchema, clearDatabase, clearRecordsOnly, force, environment, positional.ToArray());
}
