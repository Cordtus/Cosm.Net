﻿using Cosm.Net.Modules;
using Cosmos.Gov.V1Beta1;
using Grpc.Net.Client;

namespace Cosm.Net.CosmosSdk;
public partial class GovModule : IModule<GovModule, Query.QueryClient>
{
    private readonly Query.QueryClient Service;
    
    private GovModule(GrpcChannel channel)
    {
        Service = new Query.QueryClient(channel);
    }
}