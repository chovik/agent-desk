namespace AgentDesk.Portal;

public static class ProxyTrust
{
    // No trust-all switch. Public ingress must strip/overwrite X-Forwarded-Proto.
    public static IReadOnlyList<System.Net.IPNetwork> Parse(string networks)
    {
        var parsed=new List<System.Net.IPNetwork>();
        foreach(var value in networks.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries))
        {
            if(!System.Net.IPNetwork.TryParse(value,out var network)||network.PrefixLength==0)
                throw new InvalidOperationException("TrustedProxyNetworks requires explicit CIDRs; /0 is forbidden.");
            parsed.Add(network);
        }
        return parsed;
    }
}
