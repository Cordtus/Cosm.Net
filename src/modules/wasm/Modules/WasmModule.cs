﻿using Cosm.Net.Modules;
using Cosmwasm.Wasm.V1;
using Grpc.Net.Client;

namespace Cosm.Net.Wasm;
public partial class WasmModule : IModule<WasmModule, Query.QueryClient>
{
    private readonly Query.QueryClient Service;

    private WasmModule(GrpcChannel channel)
    {
        Service = new Query.QueryClient(channel);
    }

    static WasmModule IModule<WasmModule>.FromGrpcChannel(GrpcChannel channel)
        => new WasmModule(channel);
}
