using AgentDesk.Portal;
using Xunit;

public class ProxyTrustTests
{
    [Fact] public void DefaultAddsNoTrustedNetwork()=>Assert.Empty(ProxyTrust.Parse(""));
    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("::/0")]
    [InlineData("not-a-network")]
    public void InvalidOrTrustAllRejected(string input)=>Assert.Throws<InvalidOperationException>(()=>ProxyTrust.Parse(input));
    [Fact] public void ExplicitNetworksAccepted()
    {
        var values=ProxyTrust.Parse("192.0.2.0/24, 2001:db8::/64");
        Assert.Equal(2,values.Count);Assert.Equal(24,values[0].PrefixLength);
    }
}
