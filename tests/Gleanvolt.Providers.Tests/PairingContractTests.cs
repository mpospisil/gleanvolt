using Gleanvolt.Providers.Contracts;

namespace Gleanvolt.Providers.Tests;

/// <summary>Provider pairing: the secret, its hash, the code both sides show, and the collect path.</summary>
public sealed class PairingContractTests
{
    [Fact] public void ASecretIsLongAndNew() { var a = PairingSecret.Create(); Assert.True(a.Length >= 22); Assert.NotEqual(a, PairingSecret.Create()); }
    [Fact] public void TheHashIsStableAndHidesTheSecret() { var s = PairingSecret.Create(); Assert.Equal(PairingSecret.Hash(s), PairingSecret.Hash(s)); Assert.DoesNotContain(s, PairingSecret.Hash(s)); }
    [Fact] public void TheCodeIsSixDigitsInTwoGroups() => Assert.Matches(@"^\d{3} \d{3}$", PairingSecret.Code(PairingSecret.Hash(PairingSecret.Create())));
    [Fact] public void BothSidesDeriveTheSameCode() { var h = PairingSecret.Hash("fixed-secret"); Assert.Equal(PairingSecret.Code(h), PairingSecret.Code(h)); }
    [Fact] public void TheCollectPathCarriesTheRequest() => Assert.Equal("/providers/pairing/abc/collect", PairingPaths.Collect("abc"));
}
