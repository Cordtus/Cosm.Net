﻿using Cosm.Net.Wallet;
using Cosm.Net.Wasm;

namespace Cosm.Net.Test;
public class Secp256k1WalletTests
{
    private static readonly string Mnemonic = "file crane delay shadow extend outdoor maximum click approve zebra alert ten";
    private static readonly string CosmosAddress = "cosmos1wk76h3hv7vam4ppw87lw40dcy4w9z9rdndwy2j";

    [Fact]
    public void Should_Derive_Correct_Pubkey()
    {
        var wallet = new Secp256k1Wallet(Mnemonic);
        Assert.Equal(CosmosAddress, wallet.GetAddress("cosmos"));
    }
}

