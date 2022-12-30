using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Hamstix.Haby.Server.Configuration;
using Hamstix.Haby.Server.Extensions;
using Hamstix.Haby.Server.Models;
using Hamstix.Haby.Shared.Grpc.OrganizationUnits;
using Mapster;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Hamstix.Haby.Server.Grpc;

[Authorize]
public class OrganizationUnitsGrpcService : OrganizationUnitsService.OrganizationUnitsServiceBase
{
    readonly HabbyContext _context;

    public OrganizationUnitsGrpcService(
        HabbyContext context
        )
    {
        _context = context;
    }

    public override async Task<GetAllResponse> GetAll(GetAllRequest request, ServerCallContext context)
    {
        var ous = await _context.OrganizationUnits
                .AsNoTracking()
                .ToListAsync();

        var response = ous.ApplyFieldMask<OrganizationUnit, GetAllResponse, OrganizationUnitModel>(
            request.FieldMask, (response, item) => response.OrganizationUnits.Add(item));

        return response;
    }

    public override async Task<OrganizationUnitModel> GetById(GetByIdRequest request, ServerCallContext context)
    {
        var ou = await _context.OrganizationUnits.FirstOrDefaultAsync(x => x.Id == request.Id);

        if (ou is null)
            throw new RpcException(new Status(StatusCode.NotFound, $"The organization unit id {request.Id} is not found."));

        return ou.ApplyFieldMask<OrganizationUnit, OrganizationUnitModel>(request.FieldMask);
    }

    public override async Task<OrganizationUnitModel> Create(CreateRequest request, ServerCallContext context)
    {
        OrganizationUnit ou;
        if (!string.IsNullOrEmpty(request.Parents))
        {
            var parent = await GetParentOUByPath(request.Parents);
            if (parent is null)
                throw new RpcException(new Status(StatusCode.FailedPrecondition,
                    $"Can't find parent on the tree path \"{request.Parents}\"."));
            if (parent.Children.Any(x => x.Name == request.Name.Trim()))
                throw new RpcException(new Status(StatusCode.FailedPrecondition,
                    $"The organization unit name {request.Name} has already taken on the \"{parent.Name}\" level."));
            ou = new OrganizationUnit(request.Name.Trim(), parent);
        }
        else
        {
            if (await _context.OrganizationUnits.AnyAsync(x => x.Name == request.Name.Trim()))
                throw new RpcException(new Status(StatusCode.FailedPrecondition,
                    $"The organization unit name {request.Name} has already taken on the ROOT level."));
            ou = new OrganizationUnit(request.Name.Trim());
        }

        _context.OrganizationUnits.Add(ou);

        await _context.SaveChangesAsync();

        return ou.Adapt<OrganizationUnitModel>();
    }

    public override async Task<OrganizationUnitModel> Update(UpdateRequest request, ServerCallContext context)
    {
        var ou = await _context.OrganizationUnits.FirstOrDefaultAsync(x => x.Id == request.Id);

        if (ou is null)
            throw new RpcException(new Status(StatusCode.NotFound,
                $"The organization unit id {request.Id} is not found."));

        if (await _context.OrganizationUnits.AnyAsync(x => x.Name == request.OrganizationUnit.Name.Trim() && x.Id != request.Id))
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"The organization unit name {request.OrganizationUnit.Name} has already taken."));

        ou.Name = request.OrganizationUnit.Name;

        await _context.SaveChangesAsync();

        return ou.Adapt<OrganizationUnitModel>();
    }

    public override async Task<Empty> Delete(DeleteRequest request, ServerCallContext context)
    {
        var ou = await _context.OrganizationUnits.FirstOrDefaultAsync(x => x.Id == request.Id);

        if (ou is not null)
        {
            _context.OrganizationUnits.Remove(ou);

            await _context.SaveChangesAsync();
        }
        return new Empty();
    }

    public override async Task<GetOusByPathResponse> GetOusByPath(GetOusByPathRequest request, ServerCallContext context)
    {
        OrganizationUnit? parent = await GetParentOUByPath(request.OUs.ToArray());

        var rootOus = await _context.OrganizationUnits
                .AsNoTracking()
                .Include(x => x.Children)
                .Where(x => x.ParentId == null)
                .ToListAsync(); // The hack to select all records with all includes fron db.

        List<OrganizationUnit> result;
        if (parent is null)
            result = rootOus;
        else
            result = parent.Children.ToList();

        return result.ApplyFieldMask<OrganizationUnit, GetOusByPathResponse, OrganizationUnitModel>(
                request.FieldMask, (response, item) => response.OrganizationUnits.Add(item));
    }

    public override async Task<GetAllFlattenedResponse> GetAllFlattened(GetAllFlattenedRequest request, ServerCallContext context)
    {
        var leafs = await _context.OrganizationUnits
                .AsNoTracking()
                .Include(x => x.Parent)
                .ToListAsync();

        var result = new List<FlattenedOrganizationUnitModel>();
        foreach (var leaf in leafs)
        {
            var ou = new FlattenedOrganizationUnitModel { Id = leaf.Id };
            ou.FullName = AddNameToLeaf(leafs, leaf.Id, string.Empty);
            result.Add(ou);
        }

        var response = new GetAllFlattenedResponse();
        foreach (var item in result)
        {
            if (request.FieldMask is not null)
            {
                var mergedReply = new FlattenedOrganizationUnitModel();
                request.FieldMask.Merge(item, mergedReply);
            }
            else
                response.OrganizationUnits.Add(item);
        }
        return response;
    }

    string AddNameToLeaf(List<OrganizationUnit> leafs, long leafId, string fullName)
    {
        var leaf = leafs.First(x => x.Id == leafId);

        if (leaf.Parent != null)
            return AddNameToLeaf(leafs, leaf.Parent.Id, string.IsNullOrEmpty(fullName) ? leaf.Name : leaf.Name + " / " + fullName);
        else
            return string.IsNullOrEmpty(fullName) ? leaf.Name : leaf.Name + " / " + fullName;
    }

    async Task<OrganizationUnit?> GetParentOUByPath(string parents)
    {
        var splittedParents = parents.Split("/", StringSplitOptions.RemoveEmptyEntries);
        return await GetParentOUByPath(splittedParents);
    }

    async Task<OrganizationUnit?> GetParentOUByPath(string[] parents)
    {
        var allOus = await _context.OrganizationUnits
                .AsNoTracking()
                .Include(x => x.Children)
                .ToListAsync();

        var rootOus = allOus.Where(x => x.ParentId is null).ToList(); // The hack to select all records with all includes fron db.

        OrganizationUnit? parent = null;
        foreach (var requestedOu in parents)
        {
            if (parents.First() == requestedOu)
                if (rootOus.Any(x => x.Name == requestedOu))
                    parent = rootOus.First(x => x.Name == requestedOu);
                else
                    throw new RpcException(new Status(StatusCode.FailedPrecondition,
                        $"The organization unit name {requestedOu} is not found in the tree path \"{parents}\"."));
            else if (parent is not null && parent.Children.Any(x => x.Name == requestedOu))
            {
                parent = allOus.First(x => x.Id == parent.Children.First(x => x.Name == requestedOu).Id);
            }
            else
                throw new RpcException(new Status(StatusCode.FailedPrecondition,
                    $"The organization unit name {requestedOu} is not found in the tree path \"{parents}\"."));
        }

        return parent;
    }
}