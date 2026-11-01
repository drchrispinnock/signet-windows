using System.Text;
using Signet.Models;
using Signet.Services;

namespace Signet.Tests;

public class KeccakTests
{
    private static string Hex(byte[] data) => Convert.ToHexStringLower(data);

    [Fact]
    public void MatchesKnownKeccak256Vectors()
    {
        // Ethereum's Keccak-256, not SHA3-256 (which would start c5d24601 for "" too but differ for "abc").
        Assert.Equal("c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470", Hex(Keccak.Hash256([])));
        Assert.Equal("4e03657aea45a94fc7d47ba826c8d667c0d1e6e33a64a036ec44f58fa12d6c45", Hex(Keccak.Hash256("abc"u8)));
        // Longer than one 136-byte block.
        var longer = Encoding.UTF8.GetBytes(new string('a', 200));
        Assert.Equal("96ea54061def936c4be90b518992fdc6f12f535068a256229aca54267b4d084d", Hex(Keccak.Hash256(longer)));
    }

    [Fact]
    public void EtherlinkAliasMatchesTheNode()
    {
        // What node.mainnet.etherlink.com's tez_getTezosEthereumAddress returns for this address.
        Assert.Equal("0x16142132dd616dd8f61b8972ae4b9fcf8a22a450", EtherlinkAlias.AddressFor(new Address("tz1VSUr8wwNhLAzempoch5d6hLRiTh8Cjcjb")));
    }
}
