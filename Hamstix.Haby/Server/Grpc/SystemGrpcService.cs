using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Hamstix.Haby.Server.Configuration;
using Hamstix.Haby.Server.Extensions;
using Hamstix.Haby.Server.Services;
using Hamstix.Haby.Shared.Grpc.System;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Monq.Core.BasicDotNetMicroservice.Extensions;
using System.Text.Json.Nodes;

namespace Hamstix.Haby.Server.Grpc;

[Authorize]
public class SystemGrpcService : SystemService.SystemServiceBase
{
    readonly HabbyContext _context;

    public SystemGrpcService(HabbyContext context)
    {
        _context = context;
    }

    public override Task<AuthResultModel> CheckAuthToken(AclModel request, ServerCallContext context)
    {
        return Task.FromResult(new AuthResultModel { IsAuthSuccessful = true });
    }

    public override async Task<SystemVariablesModel> GetSystemVariables(Empty request, ServerCallContext context)
    {
        var systemVariables = await _context.SystemVariables
            .AsNoTracking()
            .ToListAsync();

        var configuration = new JsonObject();
        foreach (var systemVariable in systemVariables)
        {
            configuration.Add(systemVariable.Name, systemVariable.Value.CloneJsonNode());
        }

        return new SystemVariablesModel { Configuration = configuration.ToProtoStruct() };
    }

    public override async Task<SystemVariablesModel> UpdateSystemVariables(
        UpdateSystemVariablesRequest request, ServerCallContext context)
    {
        JsonObject? jsonObject;
        try
        {
            jsonObject = JsonNode.Parse(request.Configuration)?.AsObject();
        }
        catch (Exception e)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Can't parse json configuration. " +
                $"It must be valid json object. Details: {e.Message}"));
        }
        if (jsonObject is null)
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Can't parse json configuration. " +
                $"It must be valid json object."));

        var dbVariables = await _context.SystemVariables.ToListAsync();

        // Parse json object to variables.
        foreach (var jsonItem in jsonObject)
        {
            var dbVariable = dbVariables.FirstOrDefault(x => x.Name == jsonItem.Key);
            if (dbVariable is not null)
                dbVariable.Value = jsonItem.Value.CloneJsonNode();
            else
                _context.SystemVariables.Add(new Models.SystemVariable(jsonItem.Key, jsonItem.Value != null ?
                    jsonItem.Value.CloneJsonNode() : JsonValue.Create(string.Empty)!));
        }

        foreach (var dbVariable in dbVariables)
        {
            if (!jsonObject.ContainsKey(dbVariable.Name))
                _context.SystemVariables.Remove(dbVariable);
        }

        await _context.SaveChangesAsync();

        return new SystemVariablesModel { Configuration = jsonObject.ToProtoStruct() };
    }
}
