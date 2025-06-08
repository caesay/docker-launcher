using docker_launcher;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.Cors;
using Newtonsoft.Json;
using YamlConverter;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

var builder = WebApplication.CreateBuilder(args);

// Add CORS policy
builder.Services.AddCors(
    options => {
        options.AddPolicy(
            "AllowAll",
            policy =>
                policy.AllowAnyOrigin()
                    .AllowAnyHeader()
                    .AllowAnyMethod()
        );
    });

var app = builder.Build();

var port = Environment.GetEnvironmentVariable("DL_PORT");
var dockerSock = Environment.GetEnvironmentVariable("DL_DOCKER");
var pomConfig = Environment.GetEnvironmentVariable("DL_POMERIUM");
var yamlTemplate = Environment.GetEnvironmentVariable("DL_TEMPLATE");
var remoteRoot = Environment.GetEnvironmentVariable("DL_WEBROOT");
var assetsDir = Environment.GetEnvironmentVariable("DL_ASSETS");
var impersonateEmail = Environment.GetEnvironmentVariable("DL_IMPERSONATE_EMAIL");
var adminEmail = Environment.GetEnvironmentVariable("DL_ADMIN_EMAIL");
var userHeaderName = Environment.GetEnvironmentVariable("DL_AUTH_HEADER") ?? "REMOTE-USER";
var catchallSectionName = Environment.GetEnvironmentVariable("DL_CATCHALL_SECTION") ?? "Other";

var deserializer = new DeserializerBuilder()
    .IgnoreUnmatchedProperties()
    .WithNamingConvention(UnderscoredNamingConvention.Instance)
    .Build();

var containers = new ContainerApi(remoteRoot, dockerSock, pomConfig, deserializer);

if (!String.IsNullOrEmpty(port)) {
    app.Urls.Add("http://*:" + port);
}

// Use CORS globally
app.UseCors("AllowAll");

app.UseDefaultFiles();
app.UseStaticFiles();

if (!String.IsNullOrEmpty(assetsDir)) {
    app.UseStaticFiles(
        new StaticFileOptions {
            FileProvider = new PhysicalFileProvider(assetsDir),
            RequestPath = "/pubdir"
        });
}

app.MapGet(
    "/config.yml",
    async (context) => {
        context.Response.ContentType = "text/yaml";

        var yaml = File.ReadAllText(yamlTemplate);
        // var catchall = new List<ContainerApi.ContainerItem>();
        var computed = await containers.GetAllContainers();

        var authenticatedUser = context.Request.Headers.FirstOrDefault(h => h.Key.EqualsNoCase(userHeaderName)).Value.FirstOrDefault();
        if (!String.IsNullOrWhiteSpace(impersonateEmail)) {
            authenticatedUser = impersonateEmail;
        }

        if (String.IsNullOrWhiteSpace(authenticatedUser)) {
            throw new UnauthorizedAccessException("No user has been authenticated");
        }

        var isAdmin = adminEmail != null && authenticatedUser.EqualsNoCase(adminEmail);

        var filtered = computed
            .Where(c => c.AllowAllUsers || isAdmin || c.AllowUsers.Any(a => a.EqualsNoCase(authenticatedUser)))
            .ToList();

        // foreach (var item in computed.GroupBy(c => c.NetworkName)) {
        //     var networkName = item.Key;
        //     containers.SubstituteNetworkItems(ref yaml, networkName, item, ref catchall);
        // }
        //
        // if (catchall.Count > 0) {
        //     containers.SubstituteNetworkItems(ref yaml, "catch-all", catchall.ToArray(), ref catchall);
        // }

        // yaml = yaml.ReplaceLineEndings();
        // var settings = new JsonSerializerSettings() {
        //     Converters = [new FooterConverter(), new LayoutConverter(), new ColorThemeConverter(), new ParseStringConverter(), ],
        // };
        var homerObj = YamlConvert.DeserializeObject<HomerSchema>(yaml, Converter.Settings);
        var pomerium = deserializer.Deserialize<ContainerApi.PomeriumRoot>(File.ReadAllText(pomConfig));

        foreach (var item in filtered.GroupBy(c => c.Section ?? c.NetworkName)) {
            var sectionName = item.Key;
            var service = homerObj.Services.FirstOrDefault(s => s.Name.EqualsNoCase(sectionName) || $"n-{s.Name}".EqualsNoCase(sectionName));
            var itemsToAdd = item.Select(
                i => {
                    string tagstyle = "";
                    switch (i.State) {
                    case "exited":
                        tagstyle = "is-danger";
                        break;
                    case "created":
                    case "paused":
                    case "restarting":
                        tagstyle = "is-warning";
                        break;
                    case "running":
                        tagstyle = "is-success";
                        break;
                    default:
                        tagstyle = "is-info";
                        break;
                    }

                    return new Item {
                        Name = i.Name,
                        Logo = i.IconUrl,
                        Subtitle = i.Description,
                        Tag = i.State,
                        Tagstyle = tagstyle,
                        Url = i.GetNavUrl(remoteRoot) ?? "",
                    };
                }).ToArray();
            if (itemsToAdd.Any()) {
                if (service != null) {
                    service.Items = (service.Items ?? []).Concat(itemsToAdd).ToArray();
                } else {
                    var otherService = homerObj.Services.FirstOrDefault(s => s.Name.EqualsNoCase(catchallSectionName));
                    if (otherService != null) {
                        otherService.Items = (otherService.Items ?? []).Concat(itemsToAdd).ToArray();
                    } else {
                        homerObj.Services = homerObj.Services.Concat(
                        [
                            new Service() {
                                Items = itemsToAdd.ToArray(),
                                Name = sectionName,
                            }
                        ]).ToArray();
                    }
                }
            }
        }

        foreach (var s in homerObj.Services.ToArray()) {
            if (s.Items?.Any() != true) {
                homerObj.Services = homerObj.Services.Except([s]).ToArray();
            } else {
                var query =
                    from item in s.Items
                    let policy = pomerium.Policy?.FirstOrDefault(p => p.To?.Contains($"/{item.Name}:") == true) ??
                                 pomerium.Policy?.FirstOrDefault(p => p.From?.Contains($"/{item.Name}.") == true) ??
                                 pomerium.Policy?.FirstOrDefault(p => item.Url?.StartsWith(p.From?.TrimEnd('*', '/') ?? "NOT_MATCHED") == true)
                    where isAdmin ||
                          policy == null ||
                          policy.allow_any_authenticated_user ||
                          policy.allow_public_unauthenticated_access ||
                          policy.allowed_users?.Any(u => u.EqualsNoCase(authenticatedUser)) == true
                    orderby item.Name
                    select item;

                s.Items = query.ToArray();
            }
        }

        yaml = YamlConvert.SerializeObject(homerObj, Converter.Settings);

        await context.Response.WriteAsync(yaml);
    });

app.MapFallbackToFile("index.html");

app.Run();