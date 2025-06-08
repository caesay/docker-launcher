using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Docker.DotNet;
using Docker.DotNet.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace docker_launcher;

public class ContainerApi(string remoteRootHost, string dockerSock, string pomConfig, IDeserializer deserializer)
{
    private Dictionary<string, string> _networkCache = new();
    private DateTime _networkLastClearUtc = DateTime.UtcNow;
    private DockerClient dockerClient = null;
    private string hideContainers = null;

    public DockerClient GetDockerClient()
    {
        dockerClient ??= new DockerClientConfiguration(new Uri(dockerSock)).CreateClient();
        return dockerClient;
    }

    public async Task<string> GetDockerSubnet(DockerClient client, string netName)
    {
        // clear cache every 10 minutes
        var delta = DateTime.UtcNow - _networkLastClearUtc;
        if (delta > TimeSpan.FromMinutes(10))
            _networkLastClearUtc = new();

        if (_networkCache.TryGetValue(netName, out var nv))
            return nv;

        var networks = await client.Networks.ListNetworksAsync(new NetworksListParameters());

        var search = networks.FirstOrDefault(n => n.Name == netName);
        if (search == null) {
            _networkCache[netName] = null;
            return null;
        }

        var insp = await client.Networks.InspectNetworkAsync(search.ID);

        var subnet = insp?.IPAM?.Config?.FirstOrDefault();
        if (subnet?.Subnet == null) {
            _networkCache[netName] = null;
            return null;
        }

        _networkCache[netName] = subnet.Subnet;
        return subnet.Subnet;
    }

    private async Task<ContainerItem[]> MapContainerResponse(IList<ContainerListResponse> ca)
    {
        var pomerium = deserializer.Deserialize<PomeriumRoot>(File.ReadAllText(pomConfig));
        // var routes = p.Policy
        //     .Where(z => z.To != null)
        //     .ToDictionary(z => new Uri(z.To).Host, z => z.From, StringComparer.OrdinalIgnoreCase);

        var hidden = String.IsNullOrWhiteSpace(hideContainers)
            ? new string[0]
            : hideContainers.Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var query = from c in ca
            let networks = c.NetworkSettings?.Networks?.ToArray() ?? new KeyValuePair<string, EndpointSettings>[0]
            where networks.All(z => !hidden.Contains(z.Key))
            let labelNetwork = c.Labels?.Where(l => l.Key.Equals("dashboard.network")).Select(l => l.Value).FirstOrDefault()
            let labelSection = c.Labels?.Where(l => l.Key.Equals("dashboard.section")).Select(l => l.Value).FirstOrDefault()
            let labelIcon = c.Labels?.Where(l => l.Key.Equals("dashboard.icon")).Select(l => l.Value).FirstOrDefault()
            let labelUrl = c.Labels?.Where(l => l.Key.Equals("dashboard.url")).Select(l => l.Value).FirstOrDefault()
            let labelDesc = c.Labels?.Where(l => l.Key.Equals("dashboard.desc")).Select(l => l.Value).FirstOrDefault()
            let labelHidden = c.Labels?.Where(l => l.Key.Equals("dashboard.hidden")).Select(l => l.Value).FirstOrDefault()
            let labelPublic = c.Labels?.Where(l => l.Key.Equals("dashboard.public")).Select(l => l.Value).FirstOrDefault()
            let labelUsers = c.Labels?.Where(l => l.Key.Equals("dashboard.users")).Select(l => l.Value).FirstOrDefault()
            let n = (labelNetwork != null ? networks.Where(n => n.Key == labelNetwork).FirstOrDefault() : networks.FirstOrDefault())
            let name = c.Names.First().TrimStart('/')
            let policy = pomerium.Policy.Where(z => z.To != null).FirstOrDefault(pr => new Uri(pr.To).Host == name)
            where labelHidden == null || !Convert.ToBoolean(labelHidden)
            where !hidden.Contains(name)
            select new ContainerItem {
                Name = name,
                State = c.State,
                Description = labelDesc,
                Id = c.ID,
                Section = labelSection,
                IconUrl = labelIcon,
                NetworkName = n.Key,
                IpAddress = n.Value?.IPAddress,
                AllowAllUsers = policy?.allow_any_authenticated_user == true ||
                                policy?.allow_public_unauthenticated_access == true ||
                                (labelPublic != null && Convert.ToBoolean(labelPublic)),
                AllowUsers = labelUsers != null
                    ? labelUsers.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    : policy?.allowed_users ?? [],
                // NavigateUrl = extraRoute ?? (croute != null ? (launchRoutes ? $"/launch/{name}" : croute) : null),
                NavigateUrlUnsafe = labelUrl ?? policy?.From,
                Running = c.State == "running",
                Mounts = c.Mounts?.Select(z => z.Source).Where(z => !String.IsNullOrWhiteSpace(z)).ToArray() ?? new string[0],
                Ports = c.Ports.DistinctBy(p => p.PrivatePort).DistinctBy(p => p.PublicPort).OrderBy(p => p.PrivatePort).ToArray(),
                ExtraActions = new(),
            };

        var computed = query.ToArray();
        return computed.OrderBy(c => c.Name).ToArray();
    }

    public async Task<ContainerItem> GetContainer(string name)
    {
        var req = new ContainersListParameters {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>> {
                {
                    "name", new Dictionary<string, bool> {
                        { name, true }
                    }
                }
            }
        };

        var client = GetDockerClient();
        var ca = await client.Containers.ListContainersAsync(req);
        if (!ca.Any()) {
            return null;
        }

        return (await MapContainerResponse(ca)).First(c => c.Name == name);
    }

    public async Task<ContainerItem[]> GetAllContainers()
    {
        var client = GetDockerClient();
        var ca = await client.Containers.ListContainersAsync(new ContainersListParameters { All = true, Limit = 1000 });
        if (!ca.Any()) {
            return new ContainerItem[0];
        }

        return await MapContainerResponse(ca);
    }

    public void SubstituteNetworkItems(ref string dashRemoteContent, string networkName, IEnumerable<ContainerItem> item,
        ref List<ContainerItem> catchall)
    {
        if (Util.ContainsPlaceholder(dashRemoteContent, networkName)) {
            // StringBuilder sb_local = new();
            StringBuilder sb_remote = new();

            foreach (var c in item.OrderBy(c => c.Name)) {
                //            var dashItemLocal =
                //$"""
                //      - name: "{c.Name}"
                //        logo: "{c.IconUrl}"
                //        subtitle: "Hello World"
                //        url: "{c.GetNavUrl(localRoot)}"
                //""";

                string tagstyle = "";
                switch (c.State) {
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

                var dashItemRemote =
                    $"""
                          - name: "{c.Name}"
                            logo: "{c.IconUrl}"
                            subtitle: "{c.Description}"
                            tag: "{c.State}"
                            tagstyle: "{tagstyle}"
                            url: "{c.GetNavUrl(remoteRootHost)}"
                    """;

//                 if (c.Image.Contains("qbittorrent")) {
//                     dashItemRemote += "\n" +
//                                       $"""
//                                               useCredentials: true
//                                               type: "qBittorrent"
//                                               rateInterval: 2000
//                                               torrentInterval: 5000
//                                       """;
//                 } else if (c.Image.Contains("immich")) {
//                     dashItemRemote += "\n" +
//                                       $"""
//                                               type: "Immich"
//                                               apikey: "ylUt6NtB9mLrI1d9Io8N3g7oNyOqsQBTx9JdZSeiNg"
//                                               updateInterval: 10000
//                                       """;
//                 }


                //sb_local.AppendLine(dashItemLocal);
                sb_remote.AppendLine(dashItemRemote);
            }

            //ReplaceTextBetween(ref dashLocalContent, networkName, sb_local.ToString());
            Util.ReplaceTextBetween(ref dashRemoteContent, networkName, sb_remote.ToString());
        } else {
            catchall.AddRange(item);
        }
    }


    public record ContainerItem
    {
        public string Name { get; set; }
        public string State { get; set; }
        public string Description { get; set; }
        public bool? Running { get; set; }
        public string Id { get; set; }
        public string IconUrl { get; set; }
        public bool AllowAllUsers { get; set; }
        public string[] AllowUsers { get; set; }
        public string NavigateUrlUnsafe { get; set; }
        public string IpAddress { get; set; }
        public string NetworkName { get; set; }
        public string Section { get; set; }
        public string[] Mounts { get; set; }
        public Port[] Ports { get; set; }
        public Dictionary<string, string> ExtraActions { get; set; }

        public string? GetNavUrl(string rootHost)
        {
            if (NavigateUrlUnsafe == null) {
                return null;
            }

            if (NavigateUrlUnsafe.EndsWith(".*")) {
                return Regex.Replace(NavigateUrlUnsafe, @"\.\*$", "." + rootHost);
            }

            if (NavigateUrlUnsafe.Contains("*")) {
                return null;
            }

            return NavigateUrlUnsafe;
        }
    }

    public class PomeriumRoute
    {
        public string From { get; set; }
        public string To { get; set; }
        public bool allow_public_unauthenticated_access { get; set; }
        public bool allow_any_authenticated_user { get; set; }
        public string[] allowed_users { get; set; }
    }

    public class PomeriumRoot
    {
        public PomeriumRoute[] Policy { get; set; }
    }
}