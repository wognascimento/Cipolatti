using Cipolatti.API.Interfaces;
using Cipolatti.API.Models;
using Microsoft.AspNetCore.Mvc;

namespace Cipolatti.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class LookupController : Controller
    {
        private readonly ILookupRepository _lookupRepository;

        public LookupController(ILookupRepository lookupRepository)
        {
            _lookupRepository = lookupRepository;
        }

        [HttpGet("LookupBySigla")]
        public async Task<ActionResult<IEnumerable<QryLookup>>> GetLookupBySigla(string sigla)
        {
            return Ok(await _lookupRepository.lookupBySigla(sigla));
        }

        [HttpGet("lookupBySiglaByCaminhao")]
        public async Task<ActionResult<IEnumerable<QryLookup>>> GetlookupBySiglaByCaminhao(string sigla, string caminhao)
        {
            return Ok(await _lookupRepository.lookupBySiglaByCaminhao(sigla, caminhao));
        }
    }
}
