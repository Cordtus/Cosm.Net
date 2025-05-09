﻿using Cosm.Net.Client;
using Cosm.Net.Modules;
using Cosm.Net.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace Cosm.Net.Extensions;
public static class ICosmClientBuilderExtensions
{
    public static CosmClientBuilder InstallSecret(this CosmClientBuilder builder, string bech32Prefix = "secret", byte[]? encryptionSeed = null)
    {
        builder.AsInternal().ServiceCollection.AddSingleton(
            provider => new SecretEncryptionProvider(provider.GetRequiredService<IRegistrationModule>(), encryptionSeed));
        builder.AsInternal().ServiceCollection.AddSingleton<IInitializeableService, SecretEncryptionProvider>(
            provider => provider.GetRequiredService<SecretEncryptionProvider>());

        return builder
                .AsInternal().UseCosmosTxStructure()
                .AsInternal().WithChainInfo(bech32Prefix, TimeSpan.FromSeconds(90))
                .AsInternal().RegisterModulesFromAssembly(Assembly.GetExecutingAssembly());
    }
}
