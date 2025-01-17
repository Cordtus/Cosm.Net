﻿using Cosm.Net.Adapters;
using Cosm.Net.Models;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Cosm.Net.Services;
internal class ContractFactory(IWasmAdapater wasmAdapater) : IContractFactory
{
    private readonly Lock _lock = new Lock();
    private readonly IWasmAdapater _wasmAdapter = wasmAdapater;
    private readonly Dictionary<Type, Func<string, string?, IContract>> _factoryDelegates = [];

    public TContract Create<TContract>(string address, string? codeHash)
    {
        if(!_factoryDelegates.TryGetValue(typeof(TContract), out var factoryDelegate))
        {
            factoryDelegate = GetContractFactoryDelegate(typeof(TContract));
            lock(_lock)
            {
                _factoryDelegates.TryAdd(typeof(TContract), factoryDelegate);
            }
        }

        return (TContract) factoryDelegate(address, codeHash);
    }

    public void AddContractTypesFromAssembly(Assembly assembly)
    {
        var factoryDelegates = assembly.GetTypes()
            .Where(x => x.GetInterface(nameof(IContract)) is not null)
            .Where(x => x.IsInterface)
            .Select(x => (x, GetContractFactoryDelegate(x)));

        lock(_lock)
        {
            foreach(var (interfaceType, factoryDelegate) in factoryDelegates)
            {
                _factoryDelegates.TryAdd(interfaceType, factoryDelegate);
            }
        }
    }

    private Func<string, string?, IContract> GetContractFactoryDelegate(Type contractInterfaceType)
    {
        var assembly = contractInterfaceType.Assembly;
        var contractType = assembly.GetTypes()
            .Where(x => x.Name == $"{contractInterfaceType.Name}_Generated_Implementation")
            .SingleOrDefault() ?? throw new NotSupportedException($"Could not find implementation for contract interface {contractInterfaceType}");

        if(RuntimeFeature.IsDynamicCodeCompiled)
        {
            var ctor = contractType.GetConstructor([typeof(IWasmAdapater), typeof(string), typeof(string)])
                ?? throw new NotSupportedException("Constructor not found.");

            var contractAddressParam = Expression.Parameter(typeof(string), "address");
            var codeHashParam = Expression.Parameter(typeof(string), "codeHash");

            var newExpr = Expression.New(
                ctor,
                Expression.Constant(_wasmAdapter),
                contractAddressParam,
                codeHashParam
            );

            return Expression.Lambda<Func<string, string?, IContract>>(newExpr, contractAddressParam, codeHashParam).Compile();
        }
        else
        {
            return (contractAddress, codeHash) => (IContract) (Activator.CreateInstance(contractType, _wasmAdapter, contractAddress, codeHash)
                ?? throw new NotSupportedException());
        }
    }
}
