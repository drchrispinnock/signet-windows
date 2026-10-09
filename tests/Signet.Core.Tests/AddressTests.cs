using Signet.Models;

namespace Signet.Tests;

public class AddressTests
{
    [Fact]
    public void ShortensLongAddresses()
    {
        var address = new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb");
        Assert.Equal("tz1VSUr8wwN...Th8Cjcjb", address.Shortened());
        Assert.Equal("tz1...cjb", address.Shortened(prefix: 3, suffix: 3));
    }

    [Fact]
    public void LeavesShortStringsAlone()
    {
        Assert.Equal("tz1abc", new Address("tz1abc").Shortened());
        // 22 characters: exactly prefix + suffix + the ellipsis, so shortening would not save anything.
        Assert.Equal("tz1abcdefghijklmnopqrs", new Address("tz1abcdefghijklmnopqrs").Shortened());
    }

    [Theory]
    [InlineData("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb", AddressScheme.Tz1)]
    [InlineData("tz2BFTyPeYRzxd5aiBchbXN3WCZhx7BqbMBq", AddressScheme.Tz2)]
    [InlineData("tz3WXYtyDUNL91qfiCJtVUX746QpNv5i5ve5", AddressScheme.Tz3)]
    [InlineData("tz4HVR6aty9KwsQFHh81C1G7gBdhxT8kuytm", AddressScheme.Tz4)]
    public void DetectsScheme(string address, AddressScheme expected)
    {
        Assert.Equal(expected, new Address(address).Scheme);
    }

    [Fact]
    public void ContractsHaveNoScheme()
    {
        Assert.Null(new Address("KT1BRd2ka5q2cPRdXALtXD1QZ38CPam2j1ye").Scheme);
    }

    [Fact]
    public void SupportedSchemesIncludeMLDSAButNotXMSSYet()
    {
        Assert.True(AddressScheme.Tz5.IsSupported());
        Assert.NotNull(AddressScheme.Tz5.Caveat());
        Assert.False(AddressScheme.Tz6.IsSupported());
        Assert.Equal([AddressScheme.Tz1, AddressScheme.Tz2, AddressScheme.Tz3, AddressScheme.Tz4, AddressScheme.Tz5],
                     AddressSchemeExtensions.AllCases.Where(s => s.IsSupported()));
    }
}

public class AssetBalanceTests
{
    [Fact]
    public void FormatsAmountWithSymbol()
    {
        var asset = new AssetBalance("tez", new AssetBalance.AssetKind.Tez(), "Tezos", "tz", 4361.43m);
        Assert.EndsWith(" tz", asset.FormattedAmount);
        Assert.Contains("4", asset.FormattedAmount);
        Assert.Contains("43", asset.FormattedAmount);
    }
}
