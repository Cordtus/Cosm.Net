﻿using Cosm.Net.Generators.Common.SyntaxElements;
using Cosm.Net.Generators.Common.Util;
using Microsoft.CodeAnalysis;
using NJsonSchema;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Cosm.Net.Generators.CosmWasm;
public class ContractSchema
{
    [JsonPropertyName("contract_name")]
    public string ContractName { get; set; } = null!;
    [JsonPropertyName("contract_version")]
    public string ContractVersion { get; set; } = null!;
    [JsonPropertyName("idl_version")]
    public string IdlVersion { get; set; } = null!;

    [JsonPropertyName("instantiate")]
    public JsonObject Instantiate {  get; set; } = null!;
    [JsonPropertyName("execute")]
    public JsonObject Execute { get; set; } = null!;
    [JsonPropertyName("query")]
    public JsonObject Query { get; set; } = null!;
    [JsonPropertyName("migrate")]
    public JsonObject Migrate { get; set; } = null!;
    [JsonPropertyName("responses")]
    public JsonObject Responses { get; set; } = null!;

    private readonly Dictionary<string, ISyntaxBuilder> _sourceComponents = [];
    private int _requestCounter = 0;
    private int _responseCounter = 0;

    public async Task<string> GenerateCSharpCodeFileAsync(INamedTypeSymbol targetInterface)
    {
        var responseSchemas = new Dictionary<string, JsonSchema>();

        foreach(var responseNode in Responses)
        {
            responseSchemas.Add(responseNode.Key, await JsonSchema.FromJsonAsync(responseNode.Value!.ToJsonString()));
        }

        string contractClassName = targetInterface.Name;
        if (contractClassName.StartsWith("I"))
        {
            contractClassName = contractClassName.Substring(1);
        }
        else
        {
            contractClassName += "Implementation";
        }

        var contractClassBuilder = new ClassBuilder(contractClassName)
            .WithVisibility(ClassVisibility.Internal)
            .WithIsPartial(true)
            .AddField(new FieldBuilder("global::Cosm.Net.Wasm.IWasmModule", "_wasm"))
            .AddField(new FieldBuilder("global::System.String", "_contractAddress"))
            .AddBaseType("global::Cosm.Net.Wasm.Models.IContract", true)
            .AddFunctions(GenerateQueryFunctions(await JsonSchema.FromJsonAsync(Query.ToJsonString()), responseSchemas));

        var componentsSb = new StringBuilder();

        foreach(var component in _sourceComponents)
        {
            componentsSb.AppendLine(component.Value.Build());
        }

        return
            $$"""
            namespace {{targetInterface.ContainingNamespace}};
            {{contractClassBuilder.Build(generateFieldConstructor: true, generateInterface: true, interfaceName: targetInterface.Name)}}

            {{componentsSb}}
            """;
    }

    private IEnumerable<FunctionBuilder> GenerateQueryFunctions(JsonSchema queryMsgSchema,
        IReadOnlyDictionary<string, JsonSchema> responseSchemas)
    {
        foreach(var querySchema in queryMsgSchema.OneOf)
        {
            if(querySchema.Properties.Count != 1)
            {
                throw new NotSupportedException();
            }

            var argumentsSchema = querySchema.Properties.Single().Value;
            var queryName = argumentsSchema.Name;

            if(!responseSchemas.TryGetValue(queryName, out var responseSchema))
            {
                throw new NotSupportedException();
            }

            string responseType = GetOrGenerateMergingSchemaType(responseSchema);

            var functions = new List<FunctionBuilder>
            {
                new FunctionBuilder($"{NameUtils.ToValidFunctionName(queryName)}Async")
                .WithVisibility(FunctionVisibility.Public)
                .WithReturnTypeRaw($"Task<{responseType}>")
                .WithIsAsync()
                .WithSummaryComment(querySchema.Description)
                .AddStatement(new ConstructorCallBuilder("global::System.Text.Json.Nodes.JsonObject")
                    .ToVariableAssignment("innerJsonRequest"))
                .AddStatement(new ConstructorCallBuilder("global::System.Text.Json.Nodes.JsonObject")
                    .AddArgument($"""
                    [
                        new global::System.Collections.Generic.KeyValuePair<
                            global::System.String, global::System.Text.Json.Nodes.JsonNode>(
                            "{queryName}", innerJsonRequest
                        )
                    ]
                    """)
                    .ToVariableAssignment("jsonRequest"))

            };

            var requiredProps = argumentsSchema.RequiredProperties.ToList();

            var sortedProperties = argumentsSchema.Properties
                .OrderBy(x =>
                {
                    var index = requiredProps.IndexOf(x.Key);
                    if(index == -1)
                    {
                        index = int.MaxValue;
                    }
                    return index;
                });

            foreach(var property in sortedProperties)
            {
                var argName = property.Key;
                var argSchema = property.Value;

                var paramTypes = GetOrGenerateSplittingSchemaType(argSchema, queryMsgSchema).ToArray();

                if(paramTypes.Length == 0)
                {
                    throw new NotSupportedException();
                }
                else if(paramTypes.Length == 1)
                {
                    var paramType = paramTypes[0];
                    foreach(var function in functions)
                    {
                        function.AddArgument(paramType, argName, paramType.EndsWith("?"))
                                .AddStatement(new MethodCallBuilder("innerJsonRequest", "Add")
                                    .AddArgument($"\"{argName}\"")
                                    .AddArgument($"global::System.Text.Json.JsonSerializer.SerializeToNode({argName})")
                                    .Build());
                    }
                }
                else
                {
                    foreach(var function in functions.ToArray())
                    {
                        for(int i = 1; i < paramTypes.Length; i++)
                        {
                            functions.Add(function.Clone());
                        }
                    }

                    int paramIndex = 0;
                    for(int i = 0; i < functions.Count; i++)
                    {
                        paramIndex = (paramIndex + 1) % paramTypes.Length;
                        functions[i]
                            .AddArgument(paramTypes[paramIndex], argName, paramTypes[paramIndex].EndsWith("?"))
                            .AddStatement(new MethodCallBuilder("innerJsonRequest", "Add")
                                .AddArgument($"\"{argName}\"")
                                .AddArgument($"global::System.Text.Json.JsonSerializer.SerializeToNode({argName})")
                                .Build());
                    }
                }
            }

            foreach(var function in functions)
            {
                yield return function
                    .AddStatement("var encodedRequest = global::System.Text.Encoding.UTF8.GetBytes(jsonRequest.ToJsonString())")
                    .AddStatement("var encodedResponse = await _wasm.SmartContractStateAsync(_contractAddress, global::Google.Protobuf.ByteString.CopyFrom(encodedRequest))")
                    .AddStatement("var jsonResponse = global::System.Text.Encoding.UTF8.GetString(encodedResponse.Data.Span)")
                    .AddStatement($"return global::System.Text.Json.JsonSerializer.Deserialize<{responseType}>(jsonResponse)");
            }
        }
    }

    private IEnumerable<string> GetOrGenerateSplittingSchemaType(JsonSchema schema,
        JsonSchema? definitionSource = null)
    {
        definitionSource ??= schema;

        if(schema.OneOf.Count != 0 && schema.OneOf.All(x => x.IsEnumeration))
        {
            yield return GetOrGenerateEnumerationType(schema, definitionSource);
            yield break;
        }
        if(schema.OneOf.Count != 0)
        {
            foreach(var oneOfSchema in schema.OneOf)
            {
                foreach(var innerType in GetOrGenerateSplittingSchemaType(oneOfSchema, definitionSource))
                {
                    yield return innerType;
                }
            }

            yield break;
        }
        if(schema.AnyOf.Count == 2 && schema.AnyOf.Count(x => x.Type != JsonObjectType.Null) == 1)
        {
            foreach(var innerType in GetOrGenerateSplittingSchemaType(
                schema.AnyOf.Single(x => x.Type != JsonObjectType.Null), definitionSource))
            {
                yield return $"{innerType}?";
            }

            yield break;
        }
        if(schema.HasReference && schema.AllOf.Count == 0)
        {
            foreach(var innerType in GetOrGenerateSplittingSchemaType(schema.Reference, definitionSource))
            {
                yield return innerType;
            }

            yield break;
        }
        if(schema.HasReference && schema.AllOf.Count == 1)
        {
            foreach(var innerType in GetOrGenerateSplittingSchemaType(schema.AllOf.Single(), definitionSource))
            {
                yield return innerType;
            }

            yield break;
        }

        if(schema.AnyOf.Count == 0 && schema.AnyOf.Count == 0 && schema.AllOf.Count == 0 && !schema.HasReference)
        {
            switch(schema.Type)
            {
                case JsonObjectType.Array:
                    foreach(var innerType in GetOrGenerateSplittingSchemaType(schema.Item, definitionSource))
                    {
                        yield return $"{innerType}[]";
                    }
                    break;
                case JsonObjectType.Object:
                    if(schema.Properties.Count == 0)
                    {
                        yield return "object";
                        break;
                    }
                    foreach(var innerType in GetOrGenerateSplittingObjectType(schema, definitionSource))
                    {
                        yield return innerType;
                    }
                    break;
                case JsonObjectType.Boolean:
                    yield return "bool";
                    break;
                case JsonObjectType.Boolean | JsonObjectType.Null:
                    yield return "bool?";
                    break;
                case JsonObjectType.Integer:
                    yield return "int";
                    break;
                case JsonObjectType.Integer | JsonObjectType.Null:
                    yield return "int?";
                    break;
                case JsonObjectType.Number:
                    yield return "double";
                    break;
                case JsonObjectType.Number | JsonObjectType.Null:
                    yield return "double?";
                    break;
                case JsonObjectType.String:
                    yield return "string";
                    break;
                case JsonObjectType.String | JsonObjectType.Null:
                    yield return "string?";
                    break;

                case JsonObjectType.File:
                case JsonObjectType.None:
                case JsonObjectType.Null:
                default:
                    throw new NotSupportedException();
            }

            yield break;
        }

        throw new NotSupportedException();
    }

    private IEnumerable<string> GetOrGenerateSplittingObjectType(JsonSchema objectSchema, JsonSchema definitionsSource)
    {
        if(objectSchema.Type != JsonObjectType.Object || objectSchema.ActualProperties.Count == 0)
        {
            throw new InvalidOperationException();
        }

        var definitionName = definitionsSource.Definitions
            .FirstOrDefault(x => x.Value == objectSchema).Key;

        string typeName = objectSchema.Title ?? definitionName ?? $"Request{_requestCounter++}";

        if(!_sourceComponents.ContainsKey(typeName))
        {
            var classBuilder = new ClassBuilder(typeName);

            foreach(var property in objectSchema.ActualProperties)
            {
                var schemaTypes = GetOrGenerateSplittingSchemaType(property.Value, definitionsSource);

                if(schemaTypes.Count() != 1)
                {
                    //ToDo: Create abstract base class with static creator functions
                    //and create internal implementation class for each path
                    throw new NotImplementedException();
                }

                var schemaType = schemaTypes.Single();
                classBuilder.AddProperty(
                    new PropertyBuilder(
                        schemaType,
                        NameUtils.ToValidPropertyName(property.Key))
                    .WithSetterVisibility(SetterVisibility.Init)
                    .WithIsRequired(!schemaType.EndsWith("?"))
                    .WithJsonPropertyName(property.Key)
                    .WithSummaryComment(property.Value.Description)
                );
            }

            _sourceComponents.Add(typeName, classBuilder);
        }

        yield return typeName;
    }

    private string GetOrGenerateEnumerationType(JsonSchema parentSchema, JsonSchema definitionsSource)
    {
        if(parentSchema.Type != JsonObjectType.None || parentSchema.ActualProperties.Count != 0 || parentSchema.OneOf.Count == 0)
        {
            throw new InvalidOperationException();
        }

        var definitionName = definitionsSource.Definitions
            .FirstOrDefault(x => x.Value == parentSchema).Key;

        string typeName = parentSchema.Title ?? definitionName ?? throw new NotSupportedException();

        if(!_sourceComponents.ContainsKey(typeName))
        {
            var enumerationBuilder = new EnumerationBuilder(typeName)
                .WithSummaryComment(parentSchema.Description)
                .WithJsonConverter($"global::Cosm.Net.Json.SnakeCaseJsonStringEnumConverter<{typeName}>");

            foreach(var oneOf in parentSchema.OneOf)
            {
                string enumerationValue = oneOf.Enumeration.Single().ToString()
                        ?? throw new NotSupportedException();

                enumerationBuilder.AddValue(
                    NameUtils.ToValidPropertyName(enumerationValue),
                    oneOf.Description);
            }

            _sourceComponents.Add(typeName, enumerationBuilder);
        }

        return typeName;
    }

    private string GetOrGenerateMergingSchemaType(JsonSchema schema,
    JsonSchema? definitionSource = null)
    {
        definitionSource ??= schema;

        if(schema.OneOf.Count != 0 && schema.OneOf.All(x => x.IsEnumeration))
        {
            return GetOrGenerateEnumerationType(schema, definitionSource);
        }
        if(schema.OneOf.Count != 0)
        {
            return GetOrGenerateMergedType(schema, definitionSource);
        }
        if(schema.AnyOf.Count == 2 && schema.AnyOf.Count(x => x.Type != JsonObjectType.Null) == 1)
        {
            return $"{GetOrGenerateMergingSchemaType(schema.AnyOf.Single(x => x.Type != JsonObjectType.Null), definitionSource)}?";
        }
        if(schema.HasReference && schema.AllOf.Count == 0)
        {
            return GetOrGenerateMergingSchemaType(schema.Reference, definitionSource);
        }
        if(schema.HasReference && schema.AllOf.Count == 1)
        {
            return GetOrGenerateMergingSchemaType(schema.AllOf.Single(), definitionSource);
        }

        if(schema.AnyOf.Count == 0 && schema.AnyOf.Count == 0 && schema.AllOf.Count == 0 && !schema.HasReference)
        {
            switch(schema.Type)
            {
                case JsonObjectType.Array:
                    return $"{GetOrGenerateMergingSchemaType(schema.Item, definitionSource)}[]";
                case JsonObjectType.Object:
                    if(schema.Properties.Count == 0)
                    {
                        return "object";
                    }
                    return GetOrGenerateMergingObjectType(schema, definitionSource);
                case JsonObjectType.Boolean:
                    return "bool";
                case JsonObjectType.Boolean | JsonObjectType.Null:
                    return "bool?";
                case JsonObjectType.Integer:
                    return "int";
                case JsonObjectType.Integer | JsonObjectType.Null:
                    return "int?";
                case JsonObjectType.Number:
                    return "double";
                case JsonObjectType.Number | JsonObjectType.Null:
                    return "double?";
                case JsonObjectType.String:
                    return "string";
                case JsonObjectType.String | JsonObjectType.Null:
                    return "string?";

                case JsonObjectType.File:
                case JsonObjectType.None:
                case JsonObjectType.Null:
                default:
                    throw new NotSupportedException();
            }
        }

        throw new NotSupportedException();
    }

    private string GetOrGenerateMergingObjectType(JsonSchema objectSchema, JsonSchema definitionsSource)
    {
        if(objectSchema.Type != JsonObjectType.Object || objectSchema.ActualProperties.Count == 0)
        {
            throw new InvalidOperationException();
        }

        string typeName = objectSchema.Title
            ?? (objectSchema is JsonSchemaProperty p ? NameUtils.ToValidPropertyName(p.Name) : null)
            ?? definitionsSource.Definitions.FirstOrDefault(x => x.Value == objectSchema).Key
            ?? $"Response{_responseCounter++}";

        if(!_sourceComponents.ContainsKey(typeName))
        {
            var classBuilder = new ClassBuilder(typeName);

            foreach(var property in objectSchema.ActualProperties)
            {
                var schemaType = GetOrGenerateMergingSchemaType(property.Value, definitionsSource);
                classBuilder.AddProperty(
                    new PropertyBuilder(
                        schemaType,
                        NameUtils.ToValidPropertyName(property.Key))
                    .WithSetterVisibility(SetterVisibility.Init)
                    .WithIsRequired(!schemaType.EndsWith("?"))
                    .WithJsonPropertyName(property.Key)
                    .WithSummaryComment(property.Value.Description)
                );
            }

            _sourceComponents.Add(typeName, classBuilder);
        }

        return typeName;
    }

    private string GetOrGenerateMergedType(JsonSchema parentSchema, JsonSchema definitionsSource)
    {
        var typeName = definitionsSource.Definitions
            .FirstOrDefault(x => x.Value == parentSchema)
            .Key ?? $"Response{_responseCounter++}";

        if(_sourceComponents.ContainsKey(typeName))
        {
            return typeName;
        }

        var classBuilder = new ClassBuilder(typeName);

        foreach(var schema in parentSchema.OneOf)
        {
            if(schema.Properties.Count > 1)
            {
                throw new NotSupportedException();
            }

            string type = GetOrGenerateMergingSchemaType(
                schema.Properties.Count == 1 ? schema.Properties.Single().Value : schema,
                definitionsSource);

            if(IsPrimitiveType(type))
            {
                type = type == "string"
                    ? "global::Cosm.Net.Json.StringWrapper"
                    : throw new NotSupportedException();
            }

            string propertyName = schema.Properties.Count == 1
                ? schema.Properties.Single().Key
                : "Value";

            classBuilder.AddProperty(
                new PropertyBuilder(
                    $"{type.TrimEnd('?')}?",
                    NameUtils.ToValidPropertyName(propertyName)
                )
                .WithJsonPropertyName(propertyName)
                .WithSummaryComment(schema.Description)
                .WithSetterVisibility(SetterVisibility.Init)
                .WithIsRequired(false)
            );
        }

        _sourceComponents.Add(typeName, classBuilder);
        return typeName;
    }

    private static bool IsPrimitiveType(string typeName)
        => typeName switch
        {
            "string" => true,
            "int" => true,
            "double" => true,
            _ => false
        };
}
