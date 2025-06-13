using DynamicQueryEngine.Core.Models;
using DynamicQueryEngine.WebApi.Models;
using DynamicQueryEngine.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace DynamicQueryEngine.WebApi.Controllers;

[ApiController]
[Route("rules")]
public class RuleController : ControllerBase
{
    [HttpPost("evaluate")]
    public IActionResult Evaluate([FromBody] EvaluateRequest request)
    {
        try
        {
            var filtered = 
                request.Users.AsQueryable()
                             .ApplyRule(request.Rule)
                             .ToList();

            return Ok(filtered);
        }
        catch (Exception ex)
        {
            return BadRequest(new { Error = ex.Message });
        }
    }
}

public class EvaluateRequest
{
    public RuleDefinition Rule { get; set; }
    public List<User> Users { get; set; }
}