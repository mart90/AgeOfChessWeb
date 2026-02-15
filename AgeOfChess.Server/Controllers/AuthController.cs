using Microsoft.AspNetCore.Mvc;

namespace AgeOfChess.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    // POST /api/auth/register
    // POST /api/auth/login  -> returns JWT
}
