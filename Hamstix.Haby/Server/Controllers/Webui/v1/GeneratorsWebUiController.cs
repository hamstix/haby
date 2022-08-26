using Hamstix.Haby.Server.Configuration;
using Hamstix.Haby.Server.Models;
using Hamstix.Haby.Shared.Api.WebUi.v1.Generators;
using Mapster;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Monq.Core.MvcExtensions.Validation;
using Monq.Core.MvcExtensions.ViewModels;

namespace Hamstix.Haby.Server.Controllers.Webui.v1
{
    [Authorize]
    [Route("[area]/generators")]
    public class GeneratorsWebUiController : WebUiV1Controller
    {
        readonly HabbyContext _context;

        public GeneratorsWebUiController(
            HabbyContext context
            )
        {
            _context = context;
        }

        /// <summary>
        /// Get all generators.
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        public async Task<ActionResult<IEnumerable<GeneratorPreviewViewModel>>> GetAll()
        {
            var generators = await _context.Generators
                .AsNoTracking()
                .ProjectToType<GeneratorPreviewViewModel>()
                .ToListAsync();

            return generators;
        }

        /// <summary>
        /// Get generator by id.
        /// </summary>
        /// <param name="id">Generator id.</param>
        /// <returns></returns>
        [HttpGet("{id}")]
        public async Task<ActionResult<GeneratorViewModel>> Get(long id)
        {
            var generator = await _context
                .Generators
                .ProjectToType<GeneratorViewModel>()
                .FirstOrDefaultAsync(x => x.Id == id);

            if (generator is null)
                return NotFound(new ErrorResponseViewModel($"The generator id {id} is not found."));

            return generator;
        }

        /// <summary>
        /// Create new generator.
        /// </summary>
        /// <param name="value">New generator model.</param>
        /// <returns></returns>
        [HttpPost]
        [ValidateActionParameters]
        public async Task<ActionResult<GeneratorViewModel>> Add([FromBody] GeneratorPostViewModel value)
        {
            if (await _context.Generators.AnyAsync(x => x.Name == value.Name.Trim()))
                return BadRequest(new ErrorResponseViewModel($"The generator name {value.Name} has already taken."));

            var generator = new Generator(value.Name.Trim(), value.Template)
            {
                Description = value.Description,
            };

            _context.Generators.Add(generator);
            await _context.SaveChangesAsync();

            return generator.Adapt<GeneratorViewModel>();
        }

        /// <summary>
        /// Update generator.
        /// </summary>
        /// <param name="value">Updated generator model.</param>
        /// <returns></returns>
        [HttpPut("{id}")]
        [ValidateActionParameters]
        public async Task<ActionResult<GeneratorViewModel>> Update(long id, [FromBody] GeneratorPutViewModel value)
        {
            var generator = await _context.Generators.FirstOrDefaultAsync(x => x.Id == id);

            if (generator is null)
                return NotFound(new ErrorResponseViewModel($"The generator id {id} is not found."));

            if (await _context.Generators.AnyAsync(x => x.Name == value.Name.Trim() && x.Id != id))
                return BadRequest(new ErrorResponseViewModel($"The generator name {value.Name} has already taken."));

            generator.Name = value.Name.Trim();
            generator.Template = value.Template;
            generator.Description = value.Description;

            await _context.SaveChangesAsync();

            return generator.Adapt<GeneratorViewModel>();
        }
    }
}
