using BlazorWasmDotNet8AspNetCoreHosted.Server.Application;
using BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;
using Microsoft.AspNetCore.Mvc;

namespace BlazorWasmDotNet8AspNetCoreHosted.Server.Controllers;

[ApiController]
[Route("api/rules")]
// Контролер для валідації правил розкладу
public class RulesController(RulesService rules) : ControllerBase
{
    [HttpPost("validate")]
    // Повертає результат перевірки правил для пари.
    public async Task<IActionResult> Validate([FromBody] UpsertScheduleItemRequest r)
    {
        var (errors, warnings) = await rules.ValidateUpsertAsync(r);
        return Ok(new { errors, warnings });
    }
}
