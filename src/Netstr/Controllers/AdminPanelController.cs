using Microsoft.AspNetCore.Mvc;

namespace Netstr.Controllers
{
    [Route("/admin")]
    public class AdminPanelController : Controller
    {
        [HttpGet]
        public IActionResult Index()
        {
            return Redirect("/admin/index.html");
        }
    }
}
