using docker_launcher;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.Cors;
using Newtonsoft.Json;
using YamlConverter;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

var builder = WebApplication.CreateBuilder(args);

// Add CORS policy
builder.Services.AddCors(options => {
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
var uncategorizedMode = Environment.GetEnvironmentVariable("DL_UNCATEGORIZED_MODE")?.ToUpperInvariant() ?? "ADMINONLY";
var faviconUrl = Environment.GetEnvironmentVariable("DL_FAVICON_URL");
var logoutUrl = Environment.GetEnvironmentVariable("DL_LOGOUT_URL");
var hypervHost = Environment.GetEnvironmentVariable("DL_HYPERV_HOST");
var hypervUsername = Environment.GetEnvironmentVariable("DL_HYPERV_USERNAME");
var hypervPassword = Environment.GetEnvironmentVariable("DL_HYPERV_PASSWORD");
var hypervUrlTemplate = Environment.GetEnvironmentVariable("DL_HYPERV_URL");
var hypervHostName = Environment.GetEnvironmentVariable("DL_HYPERV_HOST_NAME");
var hypervHostUrl = Environment.GetEnvironmentVariable("DL_HYPERV_HOST_URL");
var vncTemplatePath = Environment.GetEnvironmentVariable("DL_VNC_TEMPLATE");

var deserializer = new DeserializerBuilder()
    .IgnoreUnmatchedProperties()
    .WithNamingConvention(UnderscoredNamingConvention.Instance)
    .Build();

var containers = new ContainerApi(remoteRoot, dockerSock, pomConfig, deserializer);

HyperVApi hyperv = null;
if (!String.IsNullOrEmpty(hypervHost) && !String.IsNullOrEmpty(hypervUsername) && !String.IsNullOrEmpty(hypervPassword)) {
    try {
        hyperv = new HyperVApi(hypervHost, hypervUsername, hypervPassword,
            app.Services.GetService<ILogger<HyperVApi>>());
    } catch (Exception ex) {
        app.Logger.LogError(ex, "Failed to initialize Hyper-V integration");
    }
}

if (!String.IsNullOrEmpty(port)) {
    app.Urls.Add("http://*:" + port);
}

// Use CORS globally
app.UseCors("AllowAll");

// Disable browser caching globally
app.Use(async (context, next) => {
    context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
    context.Response.Headers["Pragma"] = "no-cache";
    context.Response.Headers["Expires"] = "0";
    await next();
});

// Serve modified index.html with custom favicon if configured
if (!String.IsNullOrEmpty(faviconUrl)) {
    app.Use(async (context, next) => {
        if (context.Request.Path == "/" || context.Request.Path.Equals("/index.html", StringComparison.OrdinalIgnoreCase)) {
            var indexPath = Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "index.html");
            if (File.Exists(indexPath)) {
                var html = await File.ReadAllTextAsync(indexPath);
                // Replace existing favicon links with the configured URL
                html = System.Text.RegularExpressions.Regex.Replace(
                    html,
                    @"<link\s+rel=""icon""[^>]*>",
                    $@"<link rel=""icon"" href=""{faviconUrl}"">"
                );
                context.Response.ContentType = "text/html";
                await context.Response.WriteAsync(html);
                return;
            }
        }
        await next();
    });
}

app.UseDefaultFiles();
app.UseStaticFiles();

if (!String.IsNullOrEmpty(assetsDir)) {
    app.UseStaticFiles(
        new StaticFileOptions {
            FileProvider = new PhysicalFileProvider(assetsDir),
            RequestPath = "/pubdir"
        });
}

app.MapGet("/index.html", () => Results.Redirect("/", permanent: false));

app.MapGet(
    "/config.yml",
    async (context) => {
        context.Response.ContentType = "text/yaml";

        var yaml = File.ReadAllText(yamlTemplate);
        // var catchall = new List<ContainerApi.ContainerItem>();
        var computed = await containers.GetAllContainers();

        var authenticatedUser = context.Request.Headers.FirstOrDefault(h => h.Key.EqualsNoCase(userHeaderName)).Value.FirstOrDefault();
        
        if (String.IsNullOrWhiteSpace(authenticatedUser)) {
            throw new UnauthorizedAccessException("No user has been authenticated");
        }
        
        var isAdmin = adminEmail != null && authenticatedUser.EqualsNoCase(adminEmail);

        if (isAdmin && context.Request.Query.TryGetValue("as", out var asUser) && !String.IsNullOrWhiteSpace(asUser)) {
            authenticatedUser = asUser;
            isAdmin = false;
        } else if (!String.IsNullOrWhiteSpace(impersonateEmail)) {
            authenticatedUser = impersonateEmail;
            isAdmin = false;
        }

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
            var itemsToAdd = item.Select(i => {
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

                string fontAwesomeIcon = null;
                if (String.IsNullOrEmpty(i.IconUrl)) {
                    fontAwesomeIcon = "fas fa-circle-notch";
                } else if (i.IconUrl.StartsWithNoCase("fa-")) {
                    fontAwesomeIcon = $"fas {i.IconUrl}";
                } else if (i.IconUrl.StartsWithNoCase("fas ") || i.IconUrl.StartsWithNoCase("fab ")) {
                    fontAwesomeIcon = i.IconUrl;
                }

                return new Item {
                    Name = i.Name,
                    Logo = fontAwesomeIcon == null ? i.IconUrl : null,
                    Icon = fontAwesomeIcon,
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
                    // Determine if uncategorized items should be shown based on mode
                    var showUncategorized = uncategorizedMode switch {
                        "SHOW" => true,
                        "HIDE" => false,
                        "ADMINONLY" => isAdmin,
                        _ => isAdmin // default to ADMINONLY behavior
                    };

                    if (showUncategorized) {
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
        }

        // Inject Hyper-V VMs for admin users
        if (hyperv != null && isAdmin) {
            try {
                var vms = hyperv.GetAllVMs();
                var allItems = new List<Item>();

                // Add host server card if configured
                if (!string.IsNullOrWhiteSpace(hypervHostName)) {
                    var hostIp = hyperv.HostAddress;
                    string hostState, hostTagstyle, hostSubtitle;
                    if (hyperv.HostError == null) {
                        hostState = "Running";
                        hostTagstyle = "is-success";
                        hostSubtitle = hostIp;
                    } else {
                        hostState = hyperv.HostError;
                        hostTagstyle = "is-danger";
                        hostSubtitle = hyperv.HostError;
                    }
                    var hostUrlTpl = hypervHostUrl ?? hypervUrlTemplate;
                    var hostUrl = !string.IsNullOrWhiteSpace(hostUrlTpl)
                        ? hostUrlTpl.Replace("{host}", hostIp).Replace("{name}", hypervHostName)
                        : "";
                    allItems.Add(new Item {
                        Name = hypervHostName,
                        Icon = "fas fa-server",
                        Subtitle = hostSubtitle,
                        Tag = hostState,
                        Tagstyle = hostTagstyle,
                        Url = hostUrl,
                        SortOrder = 0,
                    });
                }

                // Add VM cards (sorted alphabetically)
                foreach (var vm in vms.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)) {
                    var tagstyle = vm.State switch {
                        "Running" => "is-success",
                        "Saved" or "Starting" or "Stopping" or "Paused" or "Saving" or "Reset" => "is-warning",
                        "Off" or "OffCritical" => "is-danger",
                        _ => "is-info",
                    };
                    var firstIp = vm.IPAddress.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
                    var subtitle = vm.State == "Running" && !string.IsNullOrWhiteSpace(firstIp)
                        ? firstIp
                        : vm.State;

                    // Generate URL based on VM type
                    string url;
                    if (vm.IsLinux && !string.IsNullOrWhiteSpace(vm.VagrantId)) {
                        url = $"/vnc?name={Uri.EscapeDataString(vm.Name)}";
                    } else if (!string.IsNullOrWhiteSpace(firstIp) && !string.IsNullOrWhiteSpace(hypervUrlTemplate)) {
                        url = hypervUrlTemplate.Replace("{host}", firstIp).Replace("{name}", vm.Name);
                    } else {
                        url = "";
                    }

                    // Use OS-based icons
                    var vmIcon = vm.IsLinux ? "fa-brands fa-linux" : "fa-brands fa-windows";

                    allItems.Add(new Item {
                        Name = vm.Name,
                        Icon = vmIcon,
                        Subtitle = subtitle,
                        Tag = vm.State,
                        Tagstyle = tagstyle,
                        Url = url,
                    });
                }

                if (allItems.Count > 0) {
                    var vmItems = allItems.ToArray();
                    var hvService = homerObj.Services.FirstOrDefault(s => s.Name.EqualsNoCase("HyperV"));
                    if (hvService != null) {
                        hvService.Items = (hvService.Items ?? []).Concat(vmItems).ToArray();
                    } else {
                        var catchallService = homerObj.Services.FirstOrDefault(s => s.Name.EqualsNoCase(catchallSectionName));
                        if (catchallService != null) {
                            catchallService.Items = (catchallService.Items ?? []).Concat(vmItems).ToArray();
                        } else {
                            homerObj.Services = homerObj.Services.Concat([
                                new Service {
                                    Items = vmItems,
                                    Name = "Hyper-V",
                                }
                            ]).ToArray();
                        }
                    }
                }
            } catch (Exception ex) {
                app.Logger.LogWarning(ex, "Failed to fetch Hyper-V VMs for dashboard");
            }
        }

        foreach (var s in homerObj.Services.ToArray()) {
            if (s.Items?.Any() == true) {
                var query =
                    from item in s.Items
                    let policy = pomerium.Policy?.FirstOrDefault(p => p.To?.Contains($"/{item.Name}:") == true) ??
                                 pomerium.Policy?.FirstOrDefault(p => p.From?.Contains($"/{item.Name}.") == true) ??
                                 pomerium.Policy?.FirstOrDefault(p => item.Url?.StartsWith(p.From?.TrimEnd('*', '/') ?? "NOT_MATCHED") == true)
                    // First evaluate Pomerium policy filtering
                    where isAdmin ||
                          policy == null ||
                          policy.allow_any_authenticated_user ||
                          policy.allow_public_unauthenticated_access ||
                          policy.allowed_users?.Any(u => u.EqualsNoCase(authenticatedUser)) == true
                    // Then apply YAML-level onlyAdmin filtering
                    where item.OnlyAdmin != true || isAdmin
                    // Finally apply onlyUsers filtering (only enforced when onlyAdmin is not true)
                    where item.OnlyAdmin == true ||
                          String.IsNullOrWhiteSpace(item.OnlyUsers) ||
                          item.OnlyUsers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                              .Any(u => u.EqualsNoCase(authenticatedUser))
                    orderby item.SortOrder, item.Name
                    select item;

                s.Items = query.ToArray();
            }
        }

        foreach (var s in homerObj.Services.ToArray()) {
            if (s.Items?.Any() != true) {
                homerObj.Services = homerObj.Services.Except([s]).ToArray();
            }
        }

        // Override logout URL if DL_LOGOUT_URL is configured (redirects through /logout to clear cookies)
        if (!String.IsNullOrEmpty(logoutUrl)) {
            homerObj.LogoutUrl = "/logout";
        }

        yaml = YamlConvert.SerializeObject(homerObj, Converter.Settings);

        await context.Response.WriteAsync(yaml);
    });

// Logout endpoint - clears cookies and redirects to configured URL
if (!String.IsNullOrEmpty(logoutUrl)) {
    app.MapGet("/logout", (HttpContext context) => {
        // Clear all cookies for this domain
        foreach (var cookie in context.Request.Cookies.Keys) {
            context.Response.Cookies.Delete(cookie);
        }

        // Redirect to the configured logout URL
        return Results.Redirect(logoutUrl, permanent: false);
    });
}

// VNC endpoint - generates a .vnc file with one-time password for Linux VMs
if (hyperv != null) {
    const string defaultVncTemplate = """
        [Connection]
        Host={host}
        Port=5901
        Password={password}
        """;

    app.MapGet("/vnc", async (HttpContext context) => {
        // Verify admin authentication
        var authenticatedUser = context.Request.Headers.FirstOrDefault(h => h.Key.EqualsNoCase(userHeaderName)).Value.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(authenticatedUser)) {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("Unauthorized");
            return;
        }

        var isAdmin = adminEmail != null && authenticatedUser.EqualsNoCase(adminEmail);
        if (!isAdmin) {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsync("Forbidden - Admin access required");
            return;
        }

        // Get VM name from query string
        var name = context.Request.Query["name"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(name)) {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Missing 'name' parameter");
            return;
        }

        // Lookup VM from cache
        var vm = hyperv.GetVM(name);
        if (vm == null) {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync($"VM '{name}' not found");
            return;
        }

        if (!vm.IsLinux) {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync($"VM '{name}' is not a Linux VM");
            return;
        }

        if (string.IsNullOrWhiteSpace(vm.VagrantId)) {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync($"VM '{name}' has no vagrant ID");
            return;
        }

        // Get VM IP address for host substitution
        var vmIp = vm.IPAddress.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(vmIp)) {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync($"VM '{name}' has no IP address");
            return;
        }

        try {
            // Execute vagrant ssh command to generate OTP
            var cmd = @"/opt/TurboVNC/bin/vncpasswd -o -display :1 2>&1 | grep -oP '\d+$' | /opt/TurboVNC/bin/vncpasswd -f | xxd -p";
            var password = await hyperv.ExecuteCommand($"vagrant ssh -c \"{cmd}\" {vm.VagrantId}");
            password = password.Trim();

            if (string.IsNullOrWhiteSpace(password)) {
                context.Response.StatusCode = 500;
                await context.Response.WriteAsync("Failed to generate VNC password");
                return;
            }

            // Read template from file or use default
            string template;
            if (!string.IsNullOrEmpty(vncTemplatePath) && File.Exists(vncTemplatePath)) {
                template = await File.ReadAllTextAsync(vncTemplatePath);
            } else {
                template = defaultVncTemplate;
            }

            // Replace placeholders
            var vncContent = template
                .Replace("{host}", vmIp)
                .Replace("{password}", password);

            // Return as downloadable .vnc file
            context.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{name}.vnc\"";
            context.Response.ContentType = "application/x-vnc";
            await context.Response.WriteAsync(vncContent);
        } catch (Exception ex) {
            app.Logger.LogError(ex, "Failed to generate VNC file for VM {Name}", name);
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync($"Failed to generate VNC file: {ex.Message}");
        }
    });
}

app.MapFallbackToFile("index.html");

app.Run();