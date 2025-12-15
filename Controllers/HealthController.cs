using MachineLinkConfig.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MachineLinkConfig.Controllers;

[Route("health")]
public class HealthController : Controller
{
    private readonly AppDbContext _db;
    public HealthController(AppDbContext db) => _db = db;

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        // Se questa query funziona, la connessione è OK
        var count = await _db.Plugins.CountAsync();
        return Content($"OK - Plugins in DB: {count}");
    }
}
