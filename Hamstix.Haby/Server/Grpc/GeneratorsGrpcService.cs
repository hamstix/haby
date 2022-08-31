using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Hamstix.Haby.Server.Configuration;
using Hamstix.Haby.Server.Extensions;
using Hamstix.Haby.Server.Models;
using Hamstix.Haby.Shared.Grpc.Generators;
using Mapster;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Hamstix.Haby.Server.Grpc;

[Authorize]
public class GeneratorsGrpcService : GeneratorsService.GeneratorsServiceBase
{
    readonly HabbyContext _context;

    public GeneratorsGrpcService(
        HabbyContext context
        )
    {
        _context = context;
    }

    public override async Task<GetAllResponse> GetAll(GetAllRequest request, ServerCallContext context)
    {
        var generators = await _context.Generators
                .AsNoTracking()
                .ToListAsync();

        var response = generators.ApplyFieldMask<Generator, GetAllResponse, GeneratorModel>(
            request.FieldMask, (response, item) => response.Generators.Add(item));

        return response;
    }

    public override async Task<GeneratorModel> GetById(GetByIdRequest request, ServerCallContext context)
    {
        var generator = await _context.Generators.FirstOrDefaultAsync(x => x.Id == request.Id);

        if (generator is null)
            throw new RpcException(new Status(StatusCode.NotFound, $"The generator id {request.Id} is not found."));

        return generator.ApplyFieldMask<Generator, GeneratorModel>(request.FieldMask);
    }

    public override async Task<GeneratorModel> Create(CreateRequest request, ServerCallContext context)
    {
        if (await _context.Generators.AnyAsync(x => x.Name == request.Name.Trim()))
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"The generator name {request.Name} has already taken."));

        var generator = new Generator(request.Name.Trim(), request.Template)
        {
            Description = request.Description,
        };

        _context.Generators.Add(generator);
        await _context.SaveChangesAsync();

        return generator.Adapt<GeneratorModel>();
    }

    public override async Task<GeneratorModel> Update(UpdateRequest request, ServerCallContext context)
    {
        var generator = await _context.Generators.FirstOrDefaultAsync(x => x.Id == request.Id);

        if (generator is null)
            throw new RpcException(new Status(StatusCode.NotFound,
                $"The generator id {request.Id} is not found."));

        if (await _context.Generators.AnyAsync(x => x.Name == request.Generator.Name.Trim() && x.Id != request.Id))
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"The generator name {request.Generator.Name} has already taken."));

        generator.Name = request.Generator.Name.Trim();
        generator.Template = request.Generator.Template;
        generator.Description = request.Generator.Description;

        await _context.SaveChangesAsync();

        return generator.Adapt<GeneratorModel>();
    }

    public override async Task<Empty> Delete(DeleteRequest request, ServerCallContext context)
    {
        var generator = await _context.Generators.FirstOrDefaultAsync(x => x.Id == request.Id);

        if (generator is not null)
        {
            _context.Generators.Remove(generator);

            await _context.SaveChangesAsync();
        }
        return new Empty();
    }
}