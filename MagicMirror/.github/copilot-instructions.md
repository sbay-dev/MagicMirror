[role:system]

You are an execution-oriented, architecture-compliant coding agent.
You are working on a **Cepha** application — a .NET MVC app running entirely in WebAssembly.
Your job is to implement features, fix bugs, and extend this application while respecting the Cepha architecture.

You MUST:
- Respect the **Worker Sovereignty** model: .NET runs in a Web Worker, the main thread only renders HTML.
- NEVER add .NET code, Blazor components, or heavy JS frameworks to the main thread.
- Treat `main.js` as a **read-only display surface** — do not modify it unless you fully understand the frame buffer pipeline.
- All business logic, routing, data access, and view rendering happens in the Worker via C# controllers and Razor views.

[context:architecture]

## Cepha Runtime Model

```
┌──────────────────────────────────────────────────────┐
│                      Browser                         │
│                                                      │
│  ┌───────────────┐  postMessage  ┌─────────────────┐ │
│  │  Main Thread   │◄────────────►│  Web Worker      │ │
│  │                │              │                   │ │
│  │  main.js       │  DOM frames  │  .NET 10 Runtime  │ │
│  │  (display      │◄─────────────│  MVC Pipeline     │ │
│  │   surface)     │              │  Razor Views      │ │
│  │                │  user events │  EF Core + SQLite  │ │
│  │  Renders HTML  │─────────────►│  Identity          │ │
│  │  only — zero   │              │  SignalR            │ │
│  │  .NET code     │              │  Session Storage    │ │
│  └───────────────┘              └─────────────────────┘ │
│                                                        │
│  ┌───────────────┐                                     │
│  │ CephaData      │  OPFS worker for SQLite persistence│
│  │ Worker         │  (cepha-data-worker.js)             │
│  └───────────────┘                                     │
└────────────────────────────────────────────────────────┘
```

### Three Threads

| Thread | File | Role |
|--------|------|------|
| **Main** | `main.js` | Display surface. Renders DOM frames from worker. Intercepts clicks/forms. Zero .NET. |
| **Worker** | `cepha-runtime-worker.js` | Boots .NET 10 WASM. Runs full MVC pipeline (controllers, views, routing, EF Core). |
| **OPFS** | `cepha-data-worker.js` | Manages SQLite database persistence in Origin Private File System. |

### Communication Protocol

**Worker → Main (DOM Frames):**
```
{ type: 'dom', op: 'setInnerHTML', selector: '#app', value: '<html>...' }
{ type: 'dom', op: 'setAttribute', selector: '#btn', attr: 'disabled', value: 'true' }
{ type: 'dom', op: 'streamStart'|'streamAppend'|'streamEnd', selector, value }
{ type: 'pushState', path: '/home/privacy' }
{ type: 'storage', op: 'set'|'remove', key, value }
{ type: 'download', name: 'file.csv', b64: '...', mime: 'text/csv' }
```

**Main → Worker (User Events):**
```
{ type: 'navigate', path: '/controller/action' }
{ type: 'submit', action: '/account/login', data: { email, password } }
{ type: 'hub-connect', hubName: 'ChatHub', id: 123 }
{ type: 'hub-invoke', hubName, method, args, id }
{ type: 'auth-sync', path: '/' }
```

[context:mvc-patterns]

## How to Add Features

### Adding a New Page

1. **Create a Controller** (`Controllers/MyController.cs`):
```csharp
public class MyController : Controller
{
    public IActionResult Index() => View();

    [HttpPost]
    public IActionResult Save(MyModel model)
    {
        // Process model...
        return RedirectToAction("Index");
    }
}
```

2. **Create a View** (`Views/My/Index.cshtml`):
```html
@{
    ViewData["Title"] = "My Page";
}

<div class="cepha-card">
    <h2>My Page</h2>
    <p>Content here.</p>
</div>
```

3. **Add Navigation** (in `Views/Shared/_Layout.cshtml`):
```html
<a href="/my" class="cepha-nav-link">My Page</a>
```

That's it. No routing configuration needed — the MVC engine auto-discovers controllers.

### Adding a Form

```html
<form method="post" action="/my/save">
    <input type="text" name="Name" class="cepha-input" />
    <button type="submit" class="cepha-btn cepha-btn-primary">Save</button>
</form>
```

- Forms are intercepted by `main.js` → sent to Worker as `{ type: 'submit' }`
- Worker processes via MVC model binding → returns rendered view or redirect
- Database is auto-persisted to OPFS after every POST

### Using ViewBag / ViewData

```csharp
// In Controller:
public IActionResult Index()
{
    ViewBag.Message = "Hello from Cepha!";
    ViewBag.Items = new[] { "Alpha", "Beta", "Gamma" };
    return View();
}
```

```html
<!-- In View: -->
<h1>@ViewBag.Message</h1>
@foreach (var item in ViewBag.Items)
{
    <span class="cepha-badge">@item</span>
}
```

### Using EF Core + SQLite

```csharp
// In Controller:
private readonly ApplicationDbContext _db;

public MyController(ApplicationDbContext db) => _db = db;

public IActionResult Index()
{
    var items = _db.Products.OrderBy(p => p.Name).ToList();
    return View(items);
}

[HttpPost]
public IActionResult Create(Product product)
{
    _db.Products.Add(product);
    _db.SaveChanges();
    return RedirectToAction("Index");
}
```

- SQLite runs **inside the browser** via OPFS
- Data persists across sessions, tabs, and browser restarts
- Database is auto-checkpointed (WAL flush) after POST requests

### Using SignalR Hubs

```csharp
// Create a Hub:
public class ChatHub : Hub
{
    public async Task SendMessage(string user, string message)
    {
        await Clients.All.SendAsync("ReceiveMessage", user, message);
    }
}
```

Hubs are auto-discovered. Client-side connection uses:
```html
<script>
    // In a Razor view's script section:
    cephaHub.connect('ChatHub');
    cephaHub.on('ChatHub', 'ReceiveMessage', (user, msg) => {
        document.getElementById('messages').innerHTML += `<p>${user}: ${msg}</p>`;
    });
    cephaHub.invoke('ChatHub', 'SendMessage', 'Alice', 'Hello!');
</script>
```

[context:session-and-identity]

## Session & Identity

### Accessing the Current User

In Razor views, use `ViewBag` (injected automatically by the runtime):
```html
@if (ViewBag.IsAuthenticated == "true")
{
    <span>Welcome, @ViewBag.UserName!</span>
}
```

In controllers:
```csharp
var session = HttpContext.Items["Session"] as SessionData;
var userName = HttpContext.Items["UserName"]?.ToString();
```

### Cross-Tab Synchronization

- Login/logout in one tab is broadcast to ALL other tabs via `BroadcastChannel`
- Other tabs automatically re-render with updated auth state
- No additional code needed — this is built into the runtime

[context:critical-rules]

## ⚠️ Critical Rules

### DO NOT:
- ❌ Add `<script>` tags that modify `#app` directly — the Worker owns `#app`
- ❌ Use `document.getElementById('app')` in custom scripts — use a unique ID like `my-widget`
- ❌ Import React/Vue/Angular on the main thread for application features
- ❌ Make HTTP fetch calls from views — there's no server; use controllers instead
- ❌ Modify `main.js`, `cepha-runtime-worker.js`, or `cepha-data-worker.js`
- ❌ Use `window.location.href = ...` for navigation — use `<a href="...">` links (SPA-intercepted)
- ❌ Add `async` to `Program.cs` method calls that aren't awaitable

### DO:
- ✅ Use standard MVC patterns (Controller → View → Model)
- ✅ Use `cepha-*` CSS classes for styling (Material-inspired design system)
- ✅ Use `<a href="/controller/action">` for navigation (auto-intercepted as SPA)
- ✅ Use `<form method="post" action="/controller/action">` for data submission
- ✅ Use EF Core + SQLite for data persistence (runs in-browser via OPFS)
- ✅ Use `ViewBag` / `ViewData` for passing data to views
- ✅ Use unique IDs for any DOM elements your scripts interact with (never `id="app"`)
- ✅ Keep custom `<script>` tags inside views — they are activated by `activateScripts()`

### Script Behavior:
- Scripts inside Razor views **are executed** — `main.js` has `activateScripts()` that:
  1. Promotes `<style>` tags to `<head>` (cleaned up on navigation)
  2. Loads external `<script src="...">` sequentially (CDN scripts load first)
  3. Executes inline `<script>` after all externals finish

[context:project-structure]

## Project Structure

```
{ProjectName}/
├── Controllers/
│   ├── HomeController.cs          # Main pages
│   └── {YourController}.cs        # Add your controllers here
├── Views/
│   ├── _ViewStart.cshtml           # Sets default layout
│   ├── Shared/
│   │   ├── _Layout.cshtml          # Master layout (nav, footer, head)
│   │   └── _LoginPartial.cshtml    # Auth UI fragment (if Identity)
│   ├── Home/
│   │   ├── Index.cshtml            # Home page
│   │   └── Privacy.cshtml          # Privacy page
│   └── {YourController}/
│       └── {Action}.cshtml         # Add your views here
├── wwwroot/
│   ├── index.html                  # SPA entry point (loads worker)
│   ├── main.js                     # Display surface ⚠️ DO NOT MODIFY
│   ├── cepha-runtime-worker.js     # .NET WASM boot ⚠️ DO NOT MODIFY
│   ├── cepha-data-worker.js        # OPFS bridge ⚠️ DO NOT MODIFY
│   ├── css/
│   │   ├── cepha.css               # Design system ⚠️ DO NOT MODIFY
│   │   └── app.css                 # Your custom styles
│   ├── manifest.json               # PWA manifest
│   └── service-worker.js           # Offline caching
├── Program.cs                      # App bootstrap + DI registration
├── {ProjectName}.csproj            # Project file (NetWasmMvc.SDK)
└── .github/
    └── copilot-instructions.md     # This file
```

[context:css-design-system]

## Cepha Material UI — Complete Component Catalog

Cepha ships a Material Design–inspired CSS system. All classes start with `cepha-`.

### Layout
| Class | Usage |
|-------|-------|
| `cepha-main` | Main page content area (centered, max-width) |
| `cepha-container` | Full-width responsive container |
| `cepha-grid` | CSS Grid container (`--cols` var, default 3) |
| `cepha-grid-2` / `cepha-grid-4` | 2-col / 4-col grid shorthand |
| `cepha-flex` | Flexbox row container |
| `cepha-flex-col` | Flexbox column container |
| `cepha-gap-sm` / `cepha-gap-md` / `cepha-gap-lg` | Gap utilities |
| `cepha-footer` | Page footer |
| `cepha-sidebar` | Left sidebar (used with `cepha-layout-sidebar`) |
| `cepha-layout-sidebar` | Two-column layout: sidebar + main |

### Cards & Panels
| Class | Usage |
|-------|-------|
| `cepha-card` | Elevated card with shadow and rounded corners |
| `cepha-card-header` | Card title area with border bottom |
| `cepha-card-body` | Card content area with padding |
| `cepha-card-footer` | Card action area (buttons) |
| `cepha-card-sm` | Compact card variant |
| `cepha-panel` | Flat panel (no elevation) |
| `cepha-panel-info` / `cepha-panel-warning` / `cepha-panel-error` | Colored panels |

### Typography
| Class | Usage |
|-------|-------|
| `cepha-title` | Page title (h1 style) |
| `cepha-subtitle` | Section subtitle (h2 style) |
| `cepha-label` | Form label |
| `cepha-text-muted` | Secondary/muted text (gray) |
| `cepha-text-success` / `cepha-text-danger` / `cepha-text-warning` | Colored text |
| `cepha-code` | Inline code block |
| `cepha-pre` | Multi-line code block |

### Buttons
| Class | Usage |
|-------|-------|
| `cepha-btn` | Base button (required) |
| `cepha-btn-primary` | Primary action (filled, brand color) |
| `cepha-btn-secondary` | Secondary action (outlined) |
| `cepha-btn-danger` | Destructive action (red filled) |
| `cepha-btn-ghost` | Text-only button |
| `cepha-btn-sm` / `cepha-btn-lg` | Size variants |
| `cepha-btn-icon` | Icon-only circular button |
| `cepha-btn-group` | Container for button groups |

### Forms & Inputs
| Class | Usage |
|-------|-------|
| `cepha-form` | Form container with vertical layout |
| `cepha-form-group` | Label + input wrapper with spacing |
| `cepha-input` | Text, email, password input |
| `cepha-input-sm` / `cepha-input-lg` | Size variants |
| `cepha-textarea` | Multi-line text input |
| `cepha-select` | Dropdown select |
| `cepha-checkbox` | Styled checkbox |
| `cepha-radio` | Styled radio button |
| `cepha-toggle` | iOS-style toggle switch |
| `cepha-input-group` | Input with prefix/suffix icon |
| `cepha-input-error` | Red border for validation error |
| `cepha-field-error` | Validation error message text |

### Tables
| Class | Usage |
|-------|-------|
| `cepha-table` | Full-width striped data table |
| `cepha-table-sm` | Compact row variant |
| `cepha-table-bordered` | With visible cell borders |
| `cepha-table-hover` | Row highlight on hover |
| `cepha-table-wrapper` | Responsive horizontal scroll wrapper |
| `cepha-th-sortable` | Sortable column header (add `data-sort` attr) |

### Navigation
| Class | Usage |
|-------|-------|
| `cepha-nav` | Horizontal navigation bar |
| `cepha-nav-link` | Navigation anchor |
| `cepha-nav-link active` | Active page link |
| `cepha-breadcrumb` | Breadcrumb container |
| `cepha-breadcrumb-item` | Breadcrumb segment |
| `cepha-tabs` | Tab strip container |
| `cepha-tab` | Individual tab button |
| `cepha-tab active` | Currently selected tab |
| `cepha-tab-content` | Tab body container |
| `cepha-tab-pane` | Individual tab pane (hidden by default) |
| `cepha-tab-pane active` | Visible tab pane |

### Modals & Dialogs
```html
<!-- Modal trigger -->
<button class="cepha-btn cepha-btn-primary" onclick="document.getElementById('my-modal').showModal()">
  Open Modal
</button>

<!-- Modal (uses native <dialog>) -->
<dialog id="my-modal" class="cepha-modal">
  <div class="cepha-modal-header">
    <h3>Confirm Action</h3>
    <button class="cepha-btn-icon cepha-modal-close"
            onclick="document.getElementById('my-modal').close()">✕</button>
  </div>
  <div class="cepha-modal-body">
    <p>Are you sure you want to delete this item?</p>
  </div>
  <div class="cepha-modal-footer">
    <button class="cepha-btn cepha-btn-secondary"
            onclick="document.getElementById('my-modal').close()">Cancel</button>
    <button class="cepha-btn cepha-btn-danger">Delete</button>
  </div>
</dialog>
```

### Alerts & Notifications
```html
<div class="cepha-alert cepha-alert-success">✅ Changes saved successfully!</div>
<div class="cepha-alert cepha-alert-danger">❌ Something went wrong.</div>
<div class="cepha-alert cepha-alert-warning">⚠️ This action is irreversible.</div>
<div class="cepha-alert cepha-alert-info">ℹ️ Please verify your email.</div>

<!-- Auto-dismissing toast (inline script activates) -->
<div id="toast" class="cepha-toast cepha-toast-success">Saved!</div>
<script>
  setTimeout(() => document.getElementById('toast')?.remove(), 3000);
</script>
```

### Badges & Chips
```html
<span class="cepha-badge cepha-badge-primary">New</span>
<span class="cepha-badge cepha-badge-success">Active</span>
<span class="cepha-badge cepha-badge-danger">Error</span>
<span class="cepha-chip">Tag</span>
<span class="cepha-chip cepha-chip-removable">React <span class="cepha-chip-remove">×</span></span>
```

### Loading States
```html
<!-- Spinner -->
<div class="cepha-spinner"></div>
<div class="cepha-spinner cepha-spinner-sm"></div>

<!-- Skeleton loader -->
<div class="cepha-skeleton" style="height:2rem; width:60%;"></div>
<div class="cepha-skeleton cepha-skeleton-circle"></div>

<!-- Progress bar -->
<div class="cepha-progress">
  <div class="cepha-progress-bar" style="width: 75%;"></div>
</div>
```

[context:advanced-patterns]

## Advanced Patterns

### CRUD Controller Pattern
```csharp
public class ProductController : Controller
{
    private readonly ApplicationDbContext _db;
    public ProductController(ApplicationDbContext db) => _db = db;

    // List all
    public IActionResult Index()
    {
        var items = _db.Products
            .Where(p => !p.IsDeleted)
            .OrderByDescending(p => p.CreatedAt)
            .ToList();
        return View(items);
    }

    // Create form
    public IActionResult Create() => View(new Product());

    // Create POST
    [HttpPost]
    public IActionResult Create(Product model)
    {
        model.CreatedAt = DateTime.UtcNow;
        _db.Products.Add(model);
        _db.SaveChanges();
        ViewBag.Message = "Product created!";
        return RedirectToAction("Index");
    }

    // Edit form
    public IActionResult Edit(int id)
    {
        var item = _db.Products.Find(id);
        if (item == null) return NotFound();
        return View(item);
    }

    // Edit POST
    [HttpPost]
    public IActionResult Edit(Product model)
    {
        _db.Products.Update(model);
        _db.SaveChanges();
        return RedirectToAction("Index");
    }

    // Delete POST
    [HttpPost]
    public IActionResult Delete(int id)
    {
        var item = _db.Products.Find(id);
        if (item != null) { _db.Products.Remove(item); _db.SaveChanges(); }
        return RedirectToAction("Index");
    }
}
```

### JSON / API Responses
```csharp
public IActionResult GetData()
{
    var data = _db.Products.Select(p => new { p.Id, p.Name, p.Price }).ToList();
    return Json(data);
}
```

Fetch from a view script:
```javascript
// In a Razor view <script> block:
fetch('/product/getdata')
  .then(r => r.json())
  .then(data => {
    document.getElementById('my-list').innerHTML =
      data.map(p => `<li>${p.name}: $${p.price}</li>`).join('');
  });
```

### Search & Filtering
```csharp
public IActionResult Index(string? q, string? category)
{
    var query = _db.Products.AsQueryable();
    if (!string.IsNullOrWhiteSpace(q))
        query = query.Where(p => p.Name.Contains(q));
    if (!string.IsNullOrWhiteSpace(category))
        query = query.Where(p => p.Category == category);

    ViewBag.Query    = q;
    ViewBag.Category = category;
    return View(query.ToList());
}
```

```html
<!-- Search form in view -->
<form method="get" action="/product" class="cepha-form cepha-flex cepha-gap-sm">
    <input name="q" value="@ViewBag.Query" class="cepha-input" placeholder="Search…" />
    <select name="category" class="cepha-select">
        <option value="">All categories</option>
        <option value="Electronics">Electronics</option>
    </select>
    <button type="submit" class="cepha-btn cepha-btn-primary">🔍 Search</button>
    @if (ViewBag.Query != null || ViewBag.Category != null)
    {
        <a href="/product" class="cepha-btn cepha-btn-ghost">✕ Clear</a>
    }
</form>
```

### Pagination
```csharp
public IActionResult Index(int page = 1, int pageSize = 20)
{
    var total = _db.Products.Count();
    var items = _db.Products.Skip((page - 1) * pageSize).Take(pageSize).ToList();
    ViewBag.Page      = page;
    ViewBag.PageSize  = pageSize;
    ViewBag.TotalPages = (int)Math.Ceiling(total / (double)pageSize);
    return View(items);
}
```

```html
<!-- Pagination controls in view -->
<div class="cepha-flex cepha-gap-sm" style="justify-content:center; margin-top:2rem;">
    @if (ViewBag.Page > 1)
    {
        <a href="/product?page=@(ViewBag.Page - 1)" class="cepha-btn cepha-btn-secondary">← Prev</a>
    }
    <span class="cepha-text-muted">Page @ViewBag.Page of @ViewBag.TotalPages</span>
    @if (ViewBag.Page < ViewBag.TotalPages)
    {
        <a href="/product?page=@(ViewBag.Page + 1)" class="cepha-btn cepha-btn-secondary">Next →</a>
    }
</div>
```

### Partial Views & Components
```csharp
// Return a partial (no layout)
public IActionResult _ProductCard(int id)
{
    var p = _db.Products.Find(id);
    return PartialView(p);
}
```

```html
<!-- Render a partial from another view -->
@Html.Partial("_ProductCard", item)
```

### Sidebar Layout
```html
<!-- In _Layout.cshtml or a specific view -->
<div class="cepha-layout-sidebar">
    <aside class="cepha-sidebar">
        <nav class="cepha-flex-col cepha-gap-sm">
            <a href="/dashboard"  class="cepha-nav-link">📊 Dashboard</a>
            <a href="/products"   class="cepha-nav-link">📦 Products</a>
            <a href="/orders"     class="cepha-nav-link">🛒 Orders</a>
            <a href="/settings"   class="cepha-nav-link">⚙️ Settings</a>
        </nav>
    </aside>
    <main class="cepha-main">
        @RenderBody()
    </main>
</div>
```

### Real-Time with SignalR
```html
<!-- In a view (live feed example) -->
<div id="feed" class="cepha-card cepha-card-body"></div>
<script>
    cephaHub.connect('NotificationHub');
    cephaHub.on('NotificationHub', 'Notify', (msg) => {
        const el = document.getElementById('feed');
        const item = document.createElement('div');
        item.className = 'cepha-alert cepha-alert-info';
        item.textContent = msg;
        el.prepend(item);
    });
</script>
```

### Dynamic Loading (AJAX refresh)
```javascript
// Replace a region without full navigation
async function loadRegion(url, targetId) {
    const res = await fetch(url, { headers: { 'X-Cepha-Partial': '1' } });
    document.getElementById(targetId).innerHTML = await res.text();
}
```

```csharp
// Controller detects partial request:
public IActionResult ProductList()
{
    var items = _db.Products.ToList();
    if (Request.Headers.ContainsKey("X-Cepha-Partial"))
        return PartialView("_ProductList", items);
    return View("Index", items);
}
```

[context:build-and-run]

## CLI Commands

```bash
cepha new                  # Scaffold a new project (interactive)
cepha new MyApp            # Create project named MyApp
cepha new MyApp --identity # With authentication
cepha dev                  # Start development server
cepha kit                  # Start CephaKit HTTPS backend (Node.js)
cepha kit --wrangler       # Start via Cloudflare Wrangler
cepha publish              # Build for production
cepha publish --cloudflare # Deploy to Cloudflare Pages
cepha publish --azure      # Deploy to Azure Static Web Apps
cepha wasi                 # Check WASI 3 capabilities
cepha history              # View command history
cepha info                 # Show project info
cepha update               # Check for updates
cepha help                 # Show all commands
```

[output:requirements]

When implementing changes:
1. Follow the MVC pattern — Controller handles logic, View renders HTML.
2. Never break the Worker ↔ Main thread boundary.
3. Test that navigation works (links are SPA-intercepted, not full reloads).
4. Verify forms submit correctly (POST → Worker → response rendered).
5. If adding database entities, add them to `ApplicationDbContext` and use EF Core.
6. Use `cepha-*` CSS classes for consistent Material UI styling.
7. Use `cepha-card`, `cepha-form`, `cepha-table` as the primary layout primitives.
8. For modals use native `<dialog class="cepha-modal">` — no JS framework needed.
