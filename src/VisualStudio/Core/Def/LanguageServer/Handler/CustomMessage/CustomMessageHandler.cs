﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Host.Mef;
using Roslyn.LanguageServer.Protocol;

namespace Microsoft.CodeAnalysis.LanguageServer.Handler.CustomMessage;

[ExportCSharpVisualBasicStatelessLspService(typeof(CustomMessageHandler)), Shared]
[Method(MethodName)]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
internal class CustomMessageHandler()
    : ILspServiceDocumentRequestHandler<CustomMessageParams, CustomResponse>
{
    private const string MethodName = "roslyn/customMessage";

    public bool MutatesSolutionState => false;

    public bool RequiresLSPSolution => true;

    public TextDocumentIdentifier GetTextDocumentIdentifier(CustomMessageParams request)
    {
        return request.Message.TextDocument;
    }

    public async Task<CustomResponse> HandleRequestAsync(CustomMessageParams request, RequestContext context, CancellationToken cancellationToken)
    {
        // Create the Handler instance. Requires having a parameterless constructor.
        // ```
        // public class CustomMessageHandler
        // {
        //     public Task<TResponse> ExecuteAsync(TRequest, Document, CancellationToken);
        // }
        // ```
        var handler = Activator.CreateInstanceFrom(request.AssemblyPath, request.TypeFullName).Unwrap();

        // Use reflection to find the ExecuteAsync method.
        var handlerType = handler.GetType();
        var executeMethod = handlerType.GetMethod("ExecuteAsync", BindingFlags.Public | BindingFlags.Instance);

        // CustomMessage.Message references positions in CustomMessage.TextDocument as indexes referencing CustomMessage.Positions.
        // LinePositionReadConverter allows the deserialization of these indexes into LinePosition objects.
        JsonSerializerOptions readOptions = new();
        var requestLinePositions = request.Message.Positions.Select(tdp => ProtocolConversions.PositionToLinePosition(tdp)).ToArray();
        LinePositionReadConverter linePositionReadConverter = new(requestLinePositions);
        readOptions.Converters.Add(linePositionReadConverter);

        // Deserialize the message into the expected TRequest type.
        var requestType = executeMethod.GetParameters()[0].ParameterType;
        var message = JsonSerializer.Deserialize(request.Message.Message, requestType, readOptions);

        // Invoke the execute method.
        var parameters = new object?[] { message, context.Document, cancellationToken };
        var resultTask = (Task)executeMethod.Invoke(handler, parameters);


        // Await the result and get its value.
        await resultTask.ConfigureAwait(false);
        var resultProperty = resultTask.GetType().GetProperty("Result");
        var result = resultProperty.GetValue(resultTask);

        // CustomResponse.Message must express positions in CustomMessage.TextDocument as indexes referencing CustomResponse.Positions.
        // LinePositionWriteConverter allows serializing extender-defined types into json with indexes referencing LinePosition objects.
        JsonSerializerOptions writeOptions = new();
        LinePositionWriteConverter linePositionWriteConverter = new();
        writeOptions.Converters.Add(linePositionWriteConverter);

        // Serialize the TResponse and return it to the extension.
        var responseType = resultProperty.PropertyType;
        var responseJson = JsonSerializer.Serialize(result, responseType, writeOptions);
        var responsePositions = linePositionWriteConverter.LinePositions
            .OrderBy(p => p.Value)
            .Select(p => new Position(p.Key.Line, p.Key.Character))
            .ToArray();

        return new CustomResponse(JsonNode.Parse(responseJson)!, responsePositions);
    }
}
