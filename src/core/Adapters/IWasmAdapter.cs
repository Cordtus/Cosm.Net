﻿using Cosm.Net.Models;
using Cosm.Net.Modules;
using Cosm.Net.Tx.Msg;
using Google.Protobuf;
using System.Text.Json.Nodes;

namespace Cosm.Net.Adapters;
public interface IWasmAdapater : IModule
{
    public Task<ByteString> SmartContractStateAsync(IContract contract, ByteString queryData);
    public IWasmTxMessage EncodeContractCall(IContract contract, JsonObject requestBody, IEnumerable<Coin> funds, string? txSender);
}