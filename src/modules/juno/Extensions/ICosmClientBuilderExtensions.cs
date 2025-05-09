﻿using Cosm.Net.Client;
using System.Reflection;

namespace Cosm.Net.Extensions;
public static class ICosmClientBuilderExtensions
{
    public static CosmClientBuilder InstallJuno(this CosmClientBuilder builder, string bech32Prefix = "juno")
        => builder
            .AsInternal().UseCosmosTxStructure()
            .AsInternal().WithChainInfo(bech32Prefix, TimeSpan.FromSeconds(30))
            .AsInternal().RegisterModulesFromAssembly(Assembly.GetExecutingAssembly());
}
