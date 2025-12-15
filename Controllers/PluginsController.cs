using MachineLinkConfig.Data;
using MachineLinkConfig.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace MachineLinkConfig.Controllers;

[Route("plugins")]
public class PluginsController : Controller
{
    private readonly AppDbContext _db;
    public PluginsController(AppDbContext db) => _db = db;

    // GET /plugins
    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var plugins = await _db.Plugins
            .OrderBy(p => p.DisplayName)
            .ToListAsync();

        return View(plugins);
    }
    // GET /plugins/{id}
    [HttpGet("{id:long}")]
    public async Task<IActionResult> Details(long id)
    {
        var plugin = await _db.Plugins.FirstOrDefaultAsync(p => p.Id == id);
        if (plugin == null) return NotFound();

        var machines = await _db.PluginMachineConfigs
            .Where(m => m.PluginId == id)
            .OrderBy(m => m.MachineNo)
            .ToListAsync();

        var vm = new PluginDetailsVm
        {
            Plugin = plugin,
            Machines = machines
        };

        return View(vm);
    }

    // GET /plugins/import
    [HttpGet("import")]
    public IActionResult Import()
    {
        return View(new ImportPluginsVm
        {
            EnableExportOnImported = true
        });
    }

    // POST /plugins/import
    [HttpPost("import")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Import(ImportPluginsVm vm)
    {
        vm.RawText = vm.RawText ?? "";
        var result = new ImportResultVm();

        if (string.IsNullOrWhiteSpace(vm.RawText))
        {
            result.Messages.Add("Testo vuoto.");
            vm.Result = result;
            return View(vm);
        }

        // 1) parsing righe
        var lines = vm.RawText
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.TrimEnd())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        var parsedLines = new List<ParsedLine>();

        foreach (var line in lines)
        {
            // Prova TAB
            var parts = line.Split('\t');
            if (parts.Length < 4)
            {
                // fallback: split su spazi multipli
                parts = System.Text.RegularExpressions.Regex.Split(line, @"\s{2,}");
            }

            if (parts.Length < 4)
            {
                result.SkippedLines++;
                continue;
            }

            // KEYCF è la seconda colonna, CFUSR terza, CFVAL quarta
            var keycf = (parts[1] ?? "").Trim();
            var cfusr = (parts[2] ?? "").Trim();
            var cfval = (parts[3] ?? "").Trim();

            if (string.IsNullOrWhiteSpace(keycf))
            {
                result.SkippedLines++;
                continue;
            }

            parsedLines.Add(new ParsedLine
            {
                KeyCf = keycf,
                Cfusr = string.IsNullOrWhiteSpace(cfusr) ? null : cfusr,
                Cfval = string.IsNullOrWhiteSpace(cfval) ? null : cfval
            });
        }

        // 2) deduco il plugin name (1 plugin per import)
        // Priorità: riga SURVEYS_SERVICE_PLUGIN_<NAME> / MACHINEREADER_SERVICE_PLUGIN_<NAME>
        // Fallback: dalle righe standard <ROOT>_<NAME>_01_<SUFFIX>
        var parsedKeys = parsedLines
            .Select(x => TryParseKey(x.KeyCf))
            .Where(x => x != null)
            .Cast<ParsedKey>()
            .ToList();

        var pluginName = parsedKeys
            .Where(k => k.Kind == ParsedKeyKind.PluginParam && k.IsPluginActivation)
            .Select(k => k.PluginName)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(pluginName))
        {
            pluginName = parsedKeys
                .Where(k => k.Kind == ParsedKeyKind.PluginParam && !string.IsNullOrWhiteSpace(k.PluginName))
                .Select(k => k.PluginName)
                .FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(pluginName))
        {
            result.Messages.Add("Impossibile dedurre il nome plugin dal testo (manca KEYCF riconoscibile).");
            vm.Result = result;
            return View(vm);
        }

        pluginName = pluginName.ToUpperInvariant();

        // Deduzioni: servizi supportati, dispatcher
        bool hasSurveys = parsedKeys.Any(x => x.ServiceRoot == "SURVEYS_SERVICE");
        bool hasMachineReader = parsedKeys.Any(x => x.ServiceRoot == "MACHINEREADER_SERVICE");
        bool hasDispatcher = parsedKeys.Any(x => x.Kind == ParsedKeyKind.Dispatcher);

        // dispatcherCode (se c’è almeno una riga dispatcher)
        var dispatcherCode = parsedKeys.FirstOrDefault(x => x.Kind == ParsedKeyKind.Dispatcher)?.DispatcherCode;

        // 3) Plugin upsert
        var plugin = await _db.Plugins.FirstOrDefaultAsync(p => p.Name == pluginName);
        if (plugin == null)
        {
            plugin = new Plugin
            {
                Name = pluginName,
                DisplayName = pluginName,
                SupportsSurveys = hasSurveys,
                SupportsMachineReader = hasMachineReader,
                ManagesDispatcher = hasDispatcher,
                DispatcherCode = hasDispatcher ? dispatcherCode?.ToUpperInvariant() : null
            };
            _db.Plugins.Add(plugin);
            await _db.SaveChangesAsync();
            result.PluginsCreated++;
        }
        else
        {
            bool changed = false;
            if (hasSurveys && !plugin.SupportsSurveys) { plugin.SupportsSurveys = true; changed = true; }
            if (hasMachineReader && !plugin.SupportsMachineReader) { plugin.SupportsMachineReader = true; changed = true; }
            if (hasDispatcher && !plugin.ManagesDispatcher) { plugin.ManagesDispatcher = true; changed = true; }

            if (hasDispatcher && !string.IsNullOrWhiteSpace(dispatcherCode) && string.IsNullOrWhiteSpace(plugin.DispatcherCode))
            {
                plugin.DispatcherCode = dispatcherCode!.ToUpperInvariant();
                changed = true;
            }

            if (changed)
            {
                await _db.SaveChangesAsync();
                result.PluginsUpdated++;
            }
        }

        // 4) Macchine: crea quelle che compaiono nel file
        var machineNos = parsedKeys
            .Where(x => x.Kind == ParsedKeyKind.PluginParam && x.MachineNo.HasValue)
            .Select(x => x.MachineNo!.Value)
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        var machinesDb = await _db.PluginMachineConfigs.Where(m => m.PluginId == plugin.Id).ToListAsync();

        foreach (var mn in machineNos)
        {
            if (!machinesDb.Any(m => m.MachineNo == mn))
            {
                var m = new PluginMachineConfig { PluginId = plugin.Id, MachineNo = mn, Enabled = true };
                _db.PluginMachineConfigs.Add(m);
                result.MachinesCreated++;
            }
        }

        if (result.MachinesCreated > 0)
            await _db.SaveChangesAsync();

        machinesDb = await _db.PluginMachineConfigs.Where(m => m.PluginId == plugin.Id).ToListAsync();
        var machineIdByNo = machinesDb.ToDictionary(x => x.MachineNo, x => x.Id);

        // 5) Templates + Values (solo chiavi presenti nel file)
        var templatesDb = await _db.PluginParameterTemplates.Where(t => t.PluginId == plugin.Id).ToListAsync();

        var pluginLines = parsedLines
            .Where(pl =>
            {
                var pk = TryParseKey(pl.KeyCf);
                return pk != null && string.Equals(pk.PluginName, pluginName, StringComparison.OrdinalIgnoreCase);

            })
            .ToList();
        var dispatcherLines = parsedLines
    .Where(pl =>
    {
        var pk = TryParseKey(pl.KeyCf);
        return pk != null && pk.Kind == ParsedKeyKind.Dispatcher;
    })
    .ToList();

        foreach (var pl in pluginLines)
        {
            var pk = TryParseKey(pl.KeyCf);
            if (pk == null) { result.SkippedLines++; continue; }

            if (pk.Kind == ParsedKeyKind.PluginParam)
            {
                var scope = pk.IsPluginActivation ? "PLUGIN_GLOBAL" : (pk.MachineNo.HasValue ? "PER_MACHINE" : "PLUGIN_GLOBAL");
                var keySuffix = pk.IsPluginActivation ? "PLUGIN" : pk.KeySuffix!.ToUpperInvariant();
                var serviceRoot = pk.ServiceRoot;

                // template upsert
                var tpl = templatesDb.FirstOrDefault(t =>
                    t.ServiceRoot == serviceRoot &&
                    t.Scope == scope &&
                    t.KeySuffix.ToUpper() == keySuffix);

                if (tpl == null)
                {
                    tpl = new PluginParameterTemplate
                    {
                        PluginId = plugin.Id,
                        ServiceRoot = serviceRoot,
                        Scope = scope,
                        ParamCode = keySuffix,
                        KeySuffix = keySuffix,
                        Description = pk.IsPluginActivation ? "Attivazione plugin" : "",
                        DefaultCfusr = null,
                        DefaultCfval = null,
                        ExportEnabledDefault = true,
                        SortOrder = 0
                    };
                    _db.PluginParameterTemplates.Add(tpl);
                    await _db.SaveChangesAsync();
                    templatesDb.Add(tpl);
                    result.TemplatesCreated++;
                }

                long? machineConfigId = null;
                if (scope == "PER_MACHINE" && pk.MachineNo.HasValue)
                    machineConfigId = machineIdByNo[pk.MachineNo.Value];

                // value upsert
                var existingValue = await _db.PluginParameterValues.FirstOrDefaultAsync(v =>
                    v.PluginId == plugin.Id &&
                    v.TemplateId == tpl.Id &&
                    v.MachineConfigId == machineConfigId);

                if (existingValue == null)
                {
                    _db.PluginParameterValues.Add(new PluginParameterValue
                    {
                        PluginId = plugin.Id,
                        TemplateId = tpl.Id,
                        MachineConfigId = machineConfigId,
                        Cfusr = pl.Cfusr,
                        Cfval = pl.Cfval,
                        ExportEnabled = vm.EnableExportOnImported,
                        UpdatedAt = DateTime.UtcNow
                    });
                    result.ValuesCreated++;
                }
                else
                {
                    existingValue.Cfusr = pl.Cfusr;
                    existingValue.Cfval = pl.Cfval;
                    if (vm.EnableExportOnImported) existingValue.ExportEnabled = true;
                    existingValue.UpdatedAt = DateTime.UtcNow;
                    result.ValuesUpdated++;
                }
            }
        }

        await _db.SaveChangesAsync();

        // 6) Dispatcher import (se presente)
        if (plugin.ManagesDispatcher)
        {
            // serve mappa machineCode (COB-1) -> machineId usando MAC
            var macTpl = templatesDb.FirstOrDefault(t => t.Scope == "PER_MACHINE" && t.KeySuffix.ToUpper() == "MAC");
            var machineIdByCode = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            if (macTpl != null)
            {
                var macValues = await _db.PluginParameterValues
                    .Where(v => v.PluginId == plugin.Id && v.TemplateId == macTpl.Id && v.MachineConfigId != null)
                    .ToListAsync();

                foreach (var mv in macValues)
                {
                    var code = (mv.Cfval ?? "").Trim();
                    if (!string.IsNullOrWhiteSpace(code))
                        machineIdByCode[code] = mv.MachineConfigId!.Value;
                }
            }

            var dispTemplatesDb = await _db.DispatcherParameterTemplates.Where(t => t.PluginId == plugin.Id).ToListAsync();

            foreach (var pl in dispatcherLines)

            {
                var pk = TryParseKey(pl.KeyCf);
                if (pk == null) continue;
                if (pk.Kind != ParsedKeyKind.Dispatcher) continue;
                if (!string.Equals(pk.DispatcherCode, plugin.DispatcherCode, StringComparison.OrdinalIgnoreCase))
                    continue;


                var machineCode = (pk.MachineCode ?? "").Trim();
                if (!machineIdByCode.TryGetValue(machineCode, out var machineId))
                {
                    result.Messages.Add($"Dispatcher SKIP: macchina '{machineCode}' non agganciabile (manca valore MAC). Key: {pl.KeyCf}");
                    result.SkippedLines++;
                    continue;
                }

                var paramCode = pk.ParamCode!.ToUpperInvariant();

                var dtpl = dispTemplatesDb.FirstOrDefault(t => t.ParamCode.ToUpper() == paramCode);
                if (dtpl == null)
                {
                    dtpl = new DispatcherParameterTemplate
                    {
                        PluginId = plugin.Id,
                        ParamCode = paramCode,
                        Description = "",
                        DefaultValue = null,
                        SortOrder = 0
                    };
                    _db.DispatcherParameterTemplates.Add(dtpl);
                    await _db.SaveChangesAsync();
                    dispTemplatesDb.Add(dtpl);
                    result.DispatcherTemplatesCreated++;
                }

                var dval = await _db.DispatcherParameterValues.FirstOrDefaultAsync(v =>
                    v.PluginId == plugin.Id &&
                    v.MachineConfigId == machineId &&
                    v.TemplateId == dtpl.Id);

                if (dval == null)
                {
                    _db.DispatcherParameterValues.Add(new DispatcherParameterValue
                    {
                        PluginId = plugin.Id,
                        MachineConfigId = machineId,
                        TemplateId = dtpl.Id,
                        CompanyCode = string.IsNullOrWhiteSpace(pl.Cfusr) ? "A01" : pl.Cfusr.Trim(),
                        Value = pl.Cfval,
                        ExportEnabled = vm.EnableExportOnImported,
                        UpdatedAt = DateTime.UtcNow
                    });
                    result.DispatcherValuesCreated++;
                }
                else
                {
                    dval.CompanyCode = string.IsNullOrWhiteSpace(pl.Cfusr) ? "A01" : pl.Cfusr.Trim();
                    dval.Value = pl.Cfval;
                    if (vm.EnableExportOnImported) dval.ExportEnabled = true;
                    dval.UpdatedAt = DateTime.UtcNow;
                    result.DispatcherValuesUpdated++;
                }
            }

            await _db.SaveChangesAsync();
        }

        result.Messages.Add($"Import completato per plugin {pluginName}.");
        vm.Result = result;
        return View(vm);
    }

    /* =======================
       IMPORT: helper + classi
       ======================= */

    private class ParsedLine
    {
        public string KeyCf { get; set; } = "";
        public string? Cfusr { get; set; }
        public string? Cfval { get; set; }
    }

    private enum ParsedKeyKind
    {
        PluginParam,
        Dispatcher
    }

    private class ParsedKey
    {
        public ParsedKeyKind Kind { get; set; }
        public string ServiceRoot { get; set; } = "";
        public string PluginName { get; set; } = "";

        public bool IsPluginActivation { get; set; } = false;

        public int? MachineNo { get; set; }          // 01 -> 1
        public string? KeySuffix { get; set; }       // MAC, DIP, ...

        // Dispatcher
        public string? DispatcherCode { get; set; }  // CAI
        public string? MachineCode { get; set; }     // COB-1
        public string? ParamCode { get; set; }       // ENDPOINT, USERNAME...
    }

    private static ParsedKey? TryParseKey(string keycf)
    {
        if (string.IsNullOrWhiteSpace(keycf)) return null;

        keycf = keycf.Trim();

        // DISPATCHER: SURVEYS_SERVICE_DISPATCHER_<CODE>_<MACHINECODE>_<PARAM>
        var mDisp = System.Text.RegularExpressions.Regex.Match(
            keycf,
            @"^(SURVEYS_SERVICE|MACHINEREADER_SERVICE)_DISPATCHER_([A-Z0-9]+)_([^_]+)_([A-Z0-9_]+)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (mDisp.Success)
        {
            return new ParsedKey
            {
                Kind = ParsedKeyKind.Dispatcher,
                ServiceRoot = mDisp.Groups[1].Value.ToUpperInvariant(),
                PluginName = "", // lo riempiamo dopo (per noi basta riconoscerla)
                DispatcherCode = mDisp.Groups[2].Value.ToUpperInvariant(),
                MachineCode = mDisp.Groups[3].Value,
                ParamCode = mDisp.Groups[4].Value.ToUpperInvariant()
            };
        }

        // PLUGIN activation: SURVEYS_SERVICE_PLUGIN_<PLUGIN>
        var mAct = System.Text.RegularExpressions.Regex.Match(
            keycf,
            @"^(SURVEYS_SERVICE|MACHINEREADER_SERVICE)_PLUGIN_([A-Z0-9]+)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (mAct.Success)
        {
            return new ParsedKey
            {
                Kind = ParsedKeyKind.PluginParam,
                ServiceRoot = mAct.Groups[1].Value.ToUpperInvariant(),
                PluginName = mAct.Groups[2].Value.ToUpperInvariant(),
                IsPluginActivation = true,
                MachineNo = null,
                KeySuffix = "PLUGIN"
            };
        }

        // Standard per macchina: <ROOT>_<PLUGIN>_01_<SUFFIX>
        var mStd = System.Text.RegularExpressions.Regex.Match(
            keycf,
            @"^(SURVEYS_SERVICE|MACHINEREADER_SERVICE)_([A-Z0-9]+)_([0-9]{2})_([A-Z0-9_]+)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (mStd.Success)
        {
            return new ParsedKey
            {
                Kind = ParsedKeyKind.PluginParam,
                ServiceRoot = mStd.Groups[1].Value.ToUpperInvariant(),
                PluginName = mStd.Groups[2].Value.ToUpperInvariant(),
                MachineNo = int.Parse(mStd.Groups[3].Value),
                KeySuffix = mStd.Groups[4].Value.ToUpperInvariant()
            };
        }

        return null;
    }

    // GET /plugins/{id}/templates
    [HttpGet("{id:long}/templates")]
    public async Task<IActionResult> Templates(long id)
    {
        var plugin = await _db.Plugins.FirstOrDefaultAsync(p => p.Id == id);
        if (plugin == null) return NotFound();

        var templates = await _db.PluginParameterTemplates
            .Where(t => t.PluginId == id)
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.ServiceRoot)
            .ThenBy(t => t.Scope)
            .ThenBy(t => t.KeySuffix)
            .ToListAsync();

        return View(new PluginTemplatesVm
        {
            Plugin = plugin,
            Templates = templates
        });
    }

    // GET /plugins/{id}/templates/create
    [HttpGet("{id:long}/templates/create")]
    public async Task<IActionResult> CreateTemplate(long id)
    {
        var plugin = await _db.Plugins.FirstOrDefaultAsync(p => p.Id == id);
        if (plugin == null) return NotFound();

        return View(new CreateTemplateVm
        {
            PluginId = id,
            SupportsSurveys = plugin.SupportsSurveys,
            SupportsMachineReader = plugin.SupportsMachineReader,
            ServiceRoot = plugin.SupportsSurveys ? "SURVEYS_SERVICE" : "MACHINEREADER_SERVICE",
            Scope = "PER_MACHINE",
            ExportEnabledDefault = true,
            SortOrder = 0
        });
    }
    // GET /plugins/{id}/values
    [HttpGet("{id:long}/values")]
    public async Task<IActionResult> Values(long id)
    {
        var plugin = await _db.Plugins.FirstOrDefaultAsync(p => p.Id == id);
        if (plugin == null) return NotFound();

        var machines = await _db.PluginMachineConfigs
            .Where(m => m.PluginId == id)
            .OrderBy(m => m.MachineNo)
            .ToListAsync();

        var templates = await _db.PluginParameterTemplates
            .Where(t => t.PluginId == id)
            .ToListAsync();

        // carico TUTTI i valori (servono per capire se un parametro è attivo)
        var allValues = await _db.PluginParameterValues
            .Where(v => v.PluginId == id)
            .ToListAsync();

        // questi sono quelli che finiranno nell’export
        var values = allValues
            .Where(v => v.ExportEnabled)
            .ToList();


        // Lookup veloci
        var tplById = templates.ToDictionary(t => t.Id);
        var machineById = machines.ToDictionary(m => m.Id);

        // Globali
        var globals = values
            .Where(v => v.MachineConfigId == null)
            .Select(v => MakeRow(plugin, null, tplById[v.TemplateId], v))
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.KeyCf)
            .ToList();

        // Per macchina: raggruppa
        var perMachine = machines.Select(m =>
        {
            var rows = values
                .Where(v => v.MachineConfigId == m.Id)
                .Select(v => MakeRow(plugin, m, tplById[v.TemplateId], v))
                .OrderBy(r => r.SortOrder)
                .ThenBy(r => r.KeyCf)
                .ToList();

            return new MachineBlockVm
            {
                MachineId = m.Id,
                MachineNo = m.MachineNo,
                Enabled = m.Enabled,
                Rows = rows
            };
        }).ToList();

        return View(new PluginValuesVm
        {
            Plugin = plugin,
            GlobalRows = globals,
            Machines = perMachine
        });
    }
    // GET /plugins/{id}/export
    [HttpGet("{id:long}/export")]
    public async Task<IActionResult> Export(long id)
    {
        var plugin = await _db.Plugins.FirstOrDefaultAsync(p => p.Id == id);
        if (plugin == null) return NotFound();

        var scenarios = await _db.PluginScenarios
            .Where(s => s.PluginId == id)
            .OrderBy(s => s.Name)
            .Select(s => new ScenarioPickVm { Id = s.Id, Name = s.Name })
            .ToListAsync();

        return View(new ExportVm
        {
            PluginId = id,
            IncludeHeader = false,
            IncludeDispatcher = plugin.ManagesDispatcher,
            ScenarioId = scenarios.FirstOrDefault()?.Id,
            Scenarios = scenarios
        });
    }


    // POST /plugins/{id}/export/download
    [HttpPost("{id:long}/export/download")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DownloadExport(long id, ExportVm vm)
    {
        var plugin = await _db.Plugins.FirstOrDefaultAsync(p => p.Id == id);
        if (plugin == null) return NotFound();

        // Carico templates + values
        var templates = await _db.PluginParameterTemplates
            .Where(t => t.PluginId == id)
            .ToListAsync();

        // Carico TUTTI i valori (servono per capire se un parametro IF è attivo: CFVAL == "1")
        var allValues = await _db.PluginParameterValues
            .Where(v => v.PluginId == id)
            .ToListAsync();

        // Questi sono quelli che finiranno nell’export
        var values = allValues
            .Where(v => v.ExportEnabled)
            .ToList();



        var machines = await _db.PluginMachineConfigs
            .Where(m => m.PluginId == id)
            .ToListAsync();
        // ✅ applica scenario (FORCE_VALUE + MUTEX) basandosi su:
        // - flag scenario attivi (se esistono)
        // - oppure (PRIORITARIO) parametro IF attivo quando CFVAL == "1"
        if (vm.ScenarioId.HasValue)
        {
            var scenario = await _db.PluginScenarios
                .FirstOrDefaultAsync(s => s.Id == vm.ScenarioId.Value && s.PluginId == id);

            if (scenario != null)
            {
                // Flag scenario attivi (opzionale)
                var activeFlags = await _db.PluginScenarioFlags
                    .Where(f => f.ScenarioId == scenario.Id && f.Enabled)
                    .Select(f => f.FlagCode.ToUpper())
                    .ToListAsync();

                var activeFlagSet = new HashSet<string>(activeFlags);

                var pluginRules = await _db.PluginRules
                    .Where(r => r.PluginId == id)
                    .ToListAsync();

                var globalRules = await _db.GlobalRules.ToListAsync();

                var rules = pluginRules
                    .Select(r => new { r.RuleType, r.IfParamKeySuffix, r.ThenParamKeySuffix, r.ForcedValue, r.Description })
                    .Concat(globalRules.Select(r => new { r.RuleType, r.IfParamKeySuffix, r.ThenParamKeySuffix, r.ForcedValue, r.Description }))
                    .ToList();

                // Mappa template per KeySuffix
                var templatesBySuffix = templates
                    .GroupBy(t => t.KeySuffix.ToUpperInvariant())
                    .ToDictionary(g => g.Key, g => g.ToList());

                // MUTEX -> escludiamo dall’export certe righe
                var excludedValueIds = new HashSet<long>();

                foreach (var rule in rules)
                {
                    var ifKey = rule.IfParamKeySuffix.ToUpperInvariant();
                    var thenKey = rule.ThenParamKeySuffix.ToUpperInvariant();

                    // capisco se IF è un parametro (esiste un template con quel KeySuffix)
                    bool ifIsParam = templatesBySuffix.ContainsKey(ifKey);

                    if (ifIsParam)
                    {
                        // IF è un parametro -> la regola scatta PER MACCHINA quando CFVAL == "1"
                        // prendo tutti i valori (allValues) dei template IF
                        foreach (var ifTpl in templatesBySuffix[ifKey])
                        {
                            var ifVals = allValues.Where(v => v.TemplateId == ifTpl.Id);

                            foreach (var ifVal in ifVals)
                            {
                                // attivo solo se CFVAL == "1"
                                if (!string.Equals((ifVal.Cfval ?? "").Trim(), "1", StringComparison.OrdinalIgnoreCase))
                                    continue;

                                // Applico THEN sulla STESSA macchina (MachineConfigId)
                                if (!templatesBySuffix.TryGetValue(thenKey, out var thenTpls))
                                    continue;

                                foreach (var thenTpl in thenTpls)
                                {
                                    var affected = values.Where(v =>
                                        v.TemplateId == thenTpl.Id &&
                                        v.MachineConfigId == ifVal.MachineConfigId);

                                    if (rule.RuleType == "MUTEX")
                                    {
                                        foreach (var a in affected)
                                            excludedValueIds.Add(a.Id);
                                    }
                                    else if (rule.RuleType == "FORCE_VALUE")
                                    {
                                        foreach (var a in affected)
                                            a.Cfval = rule.ForcedValue;
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        // IF NON è un parametro -> lo tratto come FLAG scenario
                        if (!activeFlagSet.Contains(ifKey))
                            continue;

                        if (!templatesBySuffix.TryGetValue(thenKey, out var thenTpls))
                            continue;

                        foreach (var thenTpl in thenTpls)
                        {
                            var affected = values.Where(v => v.TemplateId == thenTpl.Id);

                            if (rule.RuleType == "MUTEX")
                            {
                                foreach (var a in affected)
                                    excludedValueIds.Add(a.Id);
                            }
                            else if (rule.RuleType == "FORCE_VALUE")
                            {
                                foreach (var a in affected)
                                    a.Cfval = rule.ForcedValue;
                            }
                        }
                    }
                }

                // applico esclusioni MUTEX
                values = values.Where(v => !excludedValueIds.Contains(v.Id)).ToList();
            }
        }

        var tplById = templates.ToDictionary(t => t.Id);
        var machineById = machines.ToDictionary(m => m.Id);

        var sb = new StringBuilder();

        // opzionale: header
        if (vm.IncludeHeader)
            sb.AppendLine("RECORD_ID\tKEYCF\tCFUSR\tCFVAL\tCOL5\tCOL6");

        // 1) Export parametri plugin (SURVEYS/MACHINEREADER)
        foreach (var v in values.OrderBy(x => x.MachineConfigId.HasValue ? 1 : 0).ThenBy(x => x.TemplateId))
        {
            var tpl = tplById[v.TemplateId];
            PluginMachineConfig? machine = null;

            if (v.MachineConfigId.HasValue && machineById.TryGetValue(v.MachineConfigId.Value, out var m))
                machine = m;

            var keycf = BuildKeyCf(plugin, tpl, machine);
            var cfusr = v.Cfusr ?? "";
            var cfval = v.Cfval ?? "";

            // RECORD_ID vuoto, ultime 2 colonne sempre NULL
            sb.AppendLine($"\t{keycf}\t{cfusr}\t{cfval}\tNULL\tNULL");
        }

        // 2) Export Dispatcher (se richiesto e se plugin lo gestisce)
        if (plugin.ManagesDispatcher && vm.IncludeDispatcher)
        {
            // Trova il valore MAC per ogni macchina (serve per costruire la key dispatcher)
            // Regola: template PER_MACHINE con KeySuffix = "MAC"
            var macTemplate = templates.FirstOrDefault(t =>
                t.Scope == "PER_MACHINE" && t.KeySuffix.Equals("MAC", StringComparison.OrdinalIgnoreCase));

            // Se non esiste MAC, non possiamo costruire SCOS (codice macchina) -> quindi skip
            if (macTemplate != null && !string.IsNullOrWhiteSpace(plugin.DispatcherCode))
            {
                // Dispatcher values attivi
                var dispValues = await _db.DispatcherParameterValues
                    .Where(d => d.PluginId == id && d.ExportEnabled)
                    .ToListAsync();

                var dispTemplates = await _db.DispatcherParameterTemplates
                    .Where(t => t.PluginId == id)
                    .ToListAsync();

                var dispTplById = dispTemplates.ToDictionary(t => t.Id);

                // Costruisco mappa machineId -> codice macchina (CFVAL del MAC)
                var macValues = await _db.PluginParameterValues
                    .Where(v => v.PluginId == id && v.TemplateId == macTemplate.Id && v.MachineConfigId != null)
                    .ToListAsync();

                var macByMachineId = macValues
                    .Where(x => x.MachineConfigId.HasValue)
                    .ToDictionary(x => x.MachineConfigId!.Value, x => x.Cfval ?? "");

                foreach (var d in dispValues.OrderBy(x => x.MachineConfigId).ThenBy(x => x.TemplateId))
                {
                    if (!macByMachineId.TryGetValue(d.MachineConfigId, out var machineCode))
                        continue;

                    machineCode = (machineCode ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(machineCode))
                        continue; // senza MAC non possiamo generare la key

                    var dispTpl = dispTplById[d.TemplateId];
                    var keycf = $"SURVEYS_SERVICE_DISPATCHER_{plugin.DispatcherCode!.ToUpperInvariant()}_{machineCode}_{dispTpl.ParamCode.ToUpperInvariant()}";

                    var cfusr = d.CompanyCode ?? "A01"; // sul dispatcher CFUSR sempre valorizzato
                    var cfval = d.Value ?? "";

                    sb.AppendLine($"\t{keycf}\t{cfusr}\t{cfval}\tNULL\tNULL");
                }
            }
        }

        var content = sb.ToString();
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);


        var fileName = $"{plugin.Name}_export_{DateTime.UtcNow:yyyyMMdd_HHmmss}.txt";
        return File(bytes, "text/plain", fileName);
    }

    private static string BuildKeyCf(Plugin plugin, PluginParameterTemplate tpl, PluginMachineConfig? machine)
    {
        // PER_MACHINE -> include _01 / _02 ...
        // PLUGIN_GLOBAL -> no _01
        var machinePart = machine == null ? "" : $"_{machine.MachineNo:D2}";
        return $"{tpl.ServiceRoot}_{plugin.Name}{machinePart}_{tpl.KeySuffix}";
    }
    // GET /plugins/{id}/dispatcher
    [HttpGet("{id:long}/dispatcher")]
    public async Task<IActionResult> Dispatcher(long id)
    {
        var plugin = await _db.Plugins.FirstOrDefaultAsync(p => p.Id == id);
        if (plugin == null) return NotFound();
        if (!plugin.ManagesDispatcher) return BadRequest("Questo plugin non gestisce Dispatcher.");

        var dispTemplates = await _db.DispatcherParameterTemplates
            .Where(t => t.PluginId == id)
            .OrderBy(t => t.SortOrder)
            .ThenBy(t => t.ParamCode)
            .ToListAsync();

        var machines = await _db.PluginMachineConfigs
            .Where(m => m.PluginId == id)
            .OrderBy(m => m.MachineNo)
            .ToListAsync();

        var dispValues = await _db.DispatcherParameterValues
            .Where(v => v.PluginId == id)
            .ToListAsync();

        // costruisco blocchi per macchina
        var blocks = machines.Select(m =>
        {
            var rows = dispTemplates.Select(t =>
            {
                var existing = dispValues.FirstOrDefault(x => x.MachineConfigId == m.Id && x.TemplateId == t.Id);
                if (existing == null)
                {
                    // se manca la riga, la creiamo in memoria (la persistiamo nello step "ensure")
                    return new DispatcherRowVm
                    {
                        ValueId = 0,
                        MachineConfigId = m.Id,
                        MachineNo = m.MachineNo,
                        TemplateId = t.Id,
                        ParamCode = t.ParamCode,
                        Description = t.Description,
                        CompanyCode = "A01",
                        Value = t.DefaultValue,
                        ExportEnabled = true
                    };
                }

                return new DispatcherRowVm
                {
                    ValueId = existing.Id,
                    MachineConfigId = m.Id,
                    MachineNo = m.MachineNo,
                    TemplateId = t.Id,
                    ParamCode = t.ParamCode,
                    Description = t.Description,
                    CompanyCode = existing.CompanyCode,
                    Value = existing.Value,
                    ExportEnabled = existing.ExportEnabled
                };
            }).ToList();

            return new DispatcherMachineVm
            {
                MachineId = m.Id,
                MachineNo = m.MachineNo,
                Rows = rows
            };
        }).ToList();

        return View(new DispatcherPageVm
        {
            PluginId = id,
            PluginName = plugin.Name,
            DispatcherCode = plugin.DispatcherCode ?? "",
            Templates = dispTemplates.Select(t => new DispatcherTemplateVm
            {
                Id = t.Id,
                ParamCode = t.ParamCode,
                Description = t.Description,
                DefaultValue = t.DefaultValue,
                SortOrder = t.SortOrder
            }).ToList(),
            Machines = blocks
        });
    }

    // GET /plugins/{id}/dispatcher/templates/create
    [HttpGet("{id:long}/dispatcher/templates/create")]
    public async Task<IActionResult> CreateDispatcherTemplate(long id)
    {
        var plugin = await _db.Plugins.FirstOrDefaultAsync(p => p.Id == id);
        if (plugin == null) return NotFound();
        if (!plugin.ManagesDispatcher) return BadRequest("Questo plugin non gestisce Dispatcher.");

        return View(new CreateDispatcherTemplateVm
        {
            PluginId = id,
            ExportEnabledDefault = true
        });
    }

    // POST /plugins/{id}/dispatcher/templates/create
    [HttpPost("{id:long}/dispatcher/templates/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateDispatcherTemplate(long id, CreateDispatcherTemplateVm vm)
    {
        var plugin = await _db.Plugins.FirstOrDefaultAsync(p => p.Id == id);
        if (plugin == null) return NotFound();
        if (!plugin.ManagesDispatcher) return BadRequest("Questo plugin non gestisce Dispatcher.");

        vm.ParamCode = vm.ParamCode.Trim();
        vm.Description = (vm.Description ?? "").Trim();
        vm.DefaultValue = string.IsNullOrWhiteSpace(vm.DefaultValue) ? null : vm.DefaultValue.Trim();

        if (string.IsNullOrWhiteSpace(vm.ParamCode))
            ModelState.AddModelError(nameof(vm.ParamCode), "Obbligatorio.");

        if (!ModelState.IsValid)
            return View(vm);

        var duplicate = await _db.DispatcherParameterTemplates.AnyAsync(t =>
            t.PluginId == id && t.ParamCode.ToLower() == vm.ParamCode.ToLower());

        if (duplicate)
        {
            ModelState.AddModelError(nameof(vm.ParamCode), "Esiste già questo ParamCode.");
            return View(vm);
        }

        var template = new DispatcherParameterTemplate
        {
            PluginId = id,
            ParamCode = vm.ParamCode.ToUpperInvariant(),
            Description = vm.Description ?? "",
            DefaultValue = vm.DefaultValue,
            SortOrder = vm.SortOrder
        };

        _db.DispatcherParameterTemplates.Add(template);
        await _db.SaveChangesAsync(); // template.Id

        // ✅ crea una riga per ogni macchina esistente
        var machines = await _db.PluginMachineConfigs.Where(m => m.PluginId == id).ToListAsync();
        foreach (var m in machines)
        {
            _db.DispatcherParameterValues.Add(new DispatcherParameterValue
            {
                PluginId = id,
                MachineConfigId = m.Id,
                TemplateId = template.Id,
                CompanyCode = vm.DefaultCompanyCode ?? "A01",
                Value = template.DefaultValue,
                ExportEnabled = vm.ExportEnabledDefault,
                UpdatedAt = DateTime.UtcNow
            });
        }

        await _db.SaveChangesAsync();
        return Redirect($"/plugins/{id}/dispatcher");
    }

    // POST /plugins/{id}/dispatcher/save
    [HttpPost("{id:long}/dispatcher/save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveDispatcher(long id, SaveDispatcherVm vm)
    {
        var plugin = await _db.Plugins.FirstOrDefaultAsync(p => p.Id == id);
        if (plugin == null) return NotFound();
        if (!plugin.ManagesDispatcher) return BadRequest("Questo plugin non gestisce Dispatcher.");

        var ids = vm.Items.Select(x => x.ValueId).Where(x => x > 0).ToList();

        var dbValues = await _db.DispatcherParameterValues
            .Where(v => v.PluginId == id && ids.Contains(v.Id))
            .ToListAsync();

        var dict = dbValues.ToDictionary(v => v.Id);

        foreach (var item in vm.Items)
        {
            // se ValueId = 0 significa riga non persistita (caso raro), la creiamo
            if (item.ValueId <= 0)
            {
                _db.DispatcherParameterValues.Add(new DispatcherParameterValue
                {
                    PluginId = id,
                    MachineConfigId = item.MachineConfigId,
                    TemplateId = item.TemplateId,
                    CompanyCode = string.IsNullOrWhiteSpace(item.CompanyCode) ? "A01" : item.CompanyCode.Trim(),
                    Value = string.IsNullOrWhiteSpace(item.Value) ? null : item.Value.Trim(),
                    ExportEnabled = item.ExportEnabled,
                    UpdatedAt = DateTime.UtcNow
                });
                continue;
            }

            if (!dict.TryGetValue(item.ValueId, out var v)) continue;

            v.CompanyCode = string.IsNullOrWhiteSpace(item.CompanyCode) ? "A01" : item.CompanyCode.Trim();
            v.Value = string.IsNullOrWhiteSpace(item.Value) ? null : item.Value.Trim();
            v.ExportEnabled = item.ExportEnabled;
            v.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        return Redirect($"/plugins/{id}/dispatcher");
    }

    // POST /plugins/{id}/values/save
    [HttpPost("{id:long}/values/save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveValues(long id, SaveValuesVm vm)
    {
        var pluginExists = await _db.Plugins.AnyAsync(p => p.Id == id);
        if (!pluginExists) return NotFound();

        // Carichiamo dal DB solo i valori che ci arrivano
        var ids = vm.Items.Select(i => i.ValueId).ToList();

        var dbValues = await _db.PluginParameterValues
            .Where(v => v.PluginId == id && ids.Contains(v.Id))
            .ToListAsync();

        var dict = dbValues.ToDictionary(v => v.Id);

        foreach (var item in vm.Items)
        {
            if (!dict.TryGetValue(item.ValueId, out var v)) continue;

            v.Cfusr = string.IsNullOrWhiteSpace(item.Cfusr) ? null : item.Cfusr.Trim();
            v.Cfval = string.IsNullOrWhiteSpace(item.Cfval) ? null : item.Cfval.Trim();
            v.ExportEnabled = item.ExportEnabled;
            v.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();

        return Redirect($"/plugins/{id}/values");
    }

    // helper: genera KEYCF come la vuoi tu
    private static ValueRowVm MakeRow(Plugin plugin, PluginMachineConfig? machine, PluginParameterTemplate tpl, PluginParameterValue val)
    {
        string machinePart = machine == null ? "" : $"_{machine.MachineNo:D2}";
        string keycf;

        // key base: <SERVICE_ROOT>_<PLUGIN>_(NN_)<KEY_SUFFIX>
        // es: SURVEYS_SERVICE_TRUBENDRCI_01_MAC
        keycf = $"{tpl.ServiceRoot}_{plugin.Name}{machinePart}_{tpl.KeySuffix}";

        // Nota: i parametri globali che NON devono avere _01_ escono con machinePart vuoto.

        return new ValueRowVm
        {
            ValueId = val.Id,
            TemplateId = tpl.Id,
            Scope = tpl.Scope,
            ServiceRoot = tpl.ServiceRoot,
            KeyCf = keycf,
            Description = tpl.Description,
            Cfusr = val.Cfusr,
            Cfval = val.Cfval,
            ExportEnabled = val.ExportEnabled,
            SortOrder = tpl.SortOrder
        };
    }

    // POST /plugins/{id}/templates/create
    [HttpPost("{id:long}/templates/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateTemplate(long id, CreateTemplateVm vm)
    {
        var plugin = await _db.Plugins.FirstOrDefaultAsync(p => p.Id == id);
        if (plugin == null) return NotFound();

        // normalizza
        vm.ParamCode = vm.ParamCode.Trim();
        vm.KeySuffix = vm.KeySuffix.Trim();
        vm.Description = vm.Description?.Trim() ?? "";
        vm.DefaultCfusr = string.IsNullOrWhiteSpace(vm.DefaultCfusr) ? null : vm.DefaultCfusr.Trim();
        vm.DefaultCfval = string.IsNullOrWhiteSpace(vm.DefaultCfval) ? null : vm.DefaultCfval.Trim();

        // validazioni
        if (string.IsNullOrWhiteSpace(vm.ParamCode)) ModelState.AddModelError(nameof(vm.ParamCode), "Obbligatorio.");
        if (string.IsNullOrWhiteSpace(vm.KeySuffix)) ModelState.AddModelError(nameof(vm.KeySuffix), "Obbligatorio.");
        if (vm.ServiceRoot == "SURVEYS_SERVICE" && !plugin.SupportsSurveys)
            ModelState.AddModelError(nameof(vm.ServiceRoot), "Questo plugin non supporta SURVEYS_SERVICE.");
        if (vm.ServiceRoot == "MACHINEREADER_SERVICE" && !plugin.SupportsMachineReader)
            ModelState.AddModelError(nameof(vm.ServiceRoot), "Questo plugin non supporta MACHINEREADER_SERVICE.");
        if (vm.Scope != "PLUGIN_GLOBAL" && vm.Scope != "PER_MACHINE")
            ModelState.AddModelError(nameof(vm.Scope), "Scope non valido.");

        if (!ModelState.IsValid)
        {
            vm.SupportsSurveys = plugin.SupportsSurveys;
            vm.SupportsMachineReader = plugin.SupportsMachineReader;
            return View(vm);
        }

        // evita duplicati
        var duplicate = await _db.PluginParameterTemplates.AnyAsync(t =>
            t.PluginId == id &&
            t.ServiceRoot == vm.ServiceRoot &&
            t.Scope == vm.Scope &&
            t.KeySuffix.ToLower() == vm.KeySuffix.ToLower());

        if (duplicate)
        {
            ModelState.AddModelError(nameof(vm.KeySuffix), "Esiste già un parametro con questo KeySuffix (stesso servizio + scope).");
            vm.SupportsSurveys = plugin.SupportsSurveys;
            vm.SupportsMachineReader = plugin.SupportsMachineReader;
            return View(vm);
        }

        var template = new PluginParameterTemplate
        {
            PluginId = id,
            ServiceRoot = vm.ServiceRoot,
            Scope = vm.Scope,
            ParamCode = vm.ParamCode.ToUpperInvariant(),
            KeySuffix = vm.KeySuffix.ToUpperInvariant(),
            Description = vm.Description,
            DefaultCfusr = vm.DefaultCfusr,
            DefaultCfval = vm.DefaultCfval,
            ExportEnabledDefault = vm.ExportEnabledDefault,
            SortOrder = vm.SortOrder
        };

        _db.PluginParameterTemplates.Add(template);
        await _db.SaveChangesAsync(); // serve template.Id

        // ✅ crea righe valori in plugin_parameter_values
        if (template.Scope == "PLUGIN_GLOBAL")
        {
            _db.PluginParameterValues.Add(new PluginParameterValue
            {
                PluginId = id,
                TemplateId = template.Id,
                MachineConfigId = null,
                Cfusr = template.DefaultCfusr,
                Cfval = template.DefaultCfval,
                ExportEnabled = template.ExportEnabledDefault,
                UpdatedAt = DateTime.UtcNow
            });
        }
        else
        {
            var machines = await _db.PluginMachineConfigs
                .Where(m => m.PluginId == id)
                .ToListAsync();

            foreach (var m in machines)
            {
                _db.PluginParameterValues.Add(new PluginParameterValue
                {
                    PluginId = id,
                    TemplateId = template.Id,
                    MachineConfigId = m.Id,
                    Cfusr = template.DefaultCfusr,
                    Cfval = template.DefaultCfval,
                    ExportEnabled = template.ExportEnabledDefault,
                    UpdatedAt = DateTime.UtcNow
                });
            }
        }

        await _db.SaveChangesAsync();
        return Redirect($"/plugins/{id}/templates");
    }

    // POST /plugins/{id}/templates/{templateId}/delete
    [HttpPost("{id:long}/templates/{templateId:long}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteTemplate(long id, long templateId)
    {
        var t = await _db.PluginParameterTemplates.FirstOrDefaultAsync(x => x.Id == templateId && x.PluginId == id);
        if (t == null) return NotFound();

        // grazie alle FK ON DELETE CASCADE, i values legati verranno cancellati
        _db.PluginParameterTemplates.Remove(t);
        await _db.SaveChangesAsync();

        return Redirect($"/plugins/{id}/templates");
    }
    // GET /plugins/{id}/templates/{templateId}/edit
    [HttpGet("{id:long}/templates/{templateId:long}/edit")]
    public async Task<IActionResult> EditTemplate(long id, long templateId)
    {
        var plugin = await _db.Plugins.FirstOrDefaultAsync(p => p.Id == id);
        if (plugin == null) return NotFound();

        var t = await _db.PluginParameterTemplates.FirstOrDefaultAsync(x => x.Id == templateId && x.PluginId == id);
        if (t == null) return NotFound();

        return View(new EditTemplateVm
        {
            PluginId = id,
            PluginName = plugin.Name,

            TemplateId = t.Id,
            ServiceRoot = t.ServiceRoot,
            Scope = t.Scope,
            ParamCode = t.ParamCode,
            KeySuffix = t.KeySuffix,
            Description = t.Description,

            DefaultCfusr = t.DefaultCfusr,
            DefaultCfval = t.DefaultCfval,

            ExportEnabledDefault = t.ExportEnabledDefault,
            SortOrder = t.SortOrder,

            SupportsSurveys = plugin.SupportsSurveys,
            SupportsMachineReader = plugin.SupportsMachineReader
        });
    }
    // POST /plugins/{id}/templates/{templateId}/apply-defaults
    [HttpPost("{id:long}/templates/{templateId:long}/apply-defaults")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApplyTemplateDefaults(long id, long templateId)
    {
        var t = await _db.PluginParameterTemplates.FirstOrDefaultAsync(x => x.Id == templateId && x.PluginId == id);
        if (t == null) return NotFound();

        var vals = await _db.PluginParameterValues
            .Where(v => v.PluginId == id && v.TemplateId == templateId)
            .ToListAsync();

        foreach (var v in vals)
        {
            if (string.IsNullOrWhiteSpace(v.Cfusr) && !string.IsNullOrWhiteSpace(t.DefaultCfusr))
                v.Cfusr = t.DefaultCfusr;

            if (string.IsNullOrWhiteSpace(v.Cfval) && !string.IsNullOrWhiteSpace(t.DefaultCfval))
                v.Cfval = t.DefaultCfval;

            // se exportEnabled è false e il template dice di default true, abilita solo se oggi è false
            if (!v.ExportEnabled && t.ExportEnabledDefault)
                v.ExportEnabled = true;

            v.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        return Redirect($"/plugins/{id}/templates");
    }

    // POST /plugins/{id}/templates/{templateId}/edit
    [HttpPost("{id:long}/templates/{templateId:long}/edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditTemplate(long id, long templateId, EditTemplateVm vm)
    {
        var plugin = await _db.Plugins.FirstOrDefaultAsync(p => p.Id == id);
        if (plugin == null) return NotFound();

        var t = await _db.PluginParameterTemplates.FirstOrDefaultAsync(x => x.Id == templateId && x.PluginId == id);
        if (t == null) return NotFound();

        // normalizza
        vm.ParamCode = (vm.ParamCode ?? "").Trim();
        vm.KeySuffix = (vm.KeySuffix ?? "").Trim();
        vm.Description = (vm.Description ?? "").Trim();
        vm.DefaultCfusr = string.IsNullOrWhiteSpace(vm.DefaultCfusr) ? null : vm.DefaultCfusr.Trim();
        vm.DefaultCfval = string.IsNullOrWhiteSpace(vm.DefaultCfval) ? null : vm.DefaultCfval.Trim();

        // validazioni
        if (string.IsNullOrWhiteSpace(vm.ParamCode)) ModelState.AddModelError(nameof(vm.ParamCode), "Obbligatorio.");
        if (string.IsNullOrWhiteSpace(vm.KeySuffix)) ModelState.AddModelError(nameof(vm.KeySuffix), "Obbligatorio.");
        if (vm.Scope != "PLUGIN_GLOBAL" && vm.Scope != "PER_MACHINE")
            ModelState.AddModelError(nameof(vm.Scope), "Scope non valido.");

        if (vm.ServiceRoot == "SURVEYS_SERVICE" && !plugin.SupportsSurveys)
            ModelState.AddModelError(nameof(vm.ServiceRoot), "Questo plugin non supporta SURVEYS_SERVICE.");
        if (vm.ServiceRoot == "MACHINEREADER_SERVICE" && !plugin.SupportsMachineReader)
            ModelState.AddModelError(nameof(vm.ServiceRoot), "Questo plugin non supporta MACHINEREADER_SERVICE.");

        // controllo duplicati (stessa chiave: serviceRoot + scope + keySuffix)
        var duplicate = await _db.PluginParameterTemplates.AnyAsync(x =>
            x.PluginId == id &&
            x.Id != templateId &&
            x.ServiceRoot == vm.ServiceRoot &&
            x.Scope == vm.Scope &&
            x.KeySuffix.ToLower() == vm.KeySuffix.ToLower());

        if (duplicate)
            ModelState.AddModelError(nameof(vm.KeySuffix), "Esiste già un template con questo KeySuffix (stesso servizio + scope).");

        if (!ModelState.IsValid)
        {
            vm.PluginId = id;
            vm.PluginName = plugin.Name;
            vm.TemplateId = templateId;
            vm.SupportsSurveys = plugin.SupportsSurveys;
            vm.SupportsMachineReader = plugin.SupportsMachineReader;
            return View(vm);
        }

        // update template
        t.ServiceRoot = vm.ServiceRoot;
        t.Scope = vm.Scope;
        t.ParamCode = vm.ParamCode.ToUpperInvariant();
        t.KeySuffix = vm.KeySuffix.ToUpperInvariant();
        t.Description = vm.Description;
        t.DefaultCfusr = vm.DefaultCfusr;
        t.DefaultCfval = vm.DefaultCfval;
        t.ExportEnabledDefault = vm.ExportEnabledDefault;
        t.SortOrder = vm.SortOrder;

        await _db.SaveChangesAsync();

        return Redirect($"/plugins/{id}/templates");
    }

    [HttpPost("{id:long}/machines/add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddMachine(long id)
    {
        var plugin = await _db.Plugins.FirstOrDefaultAsync(p => p.Id == id);
        if (plugin == null) return NotFound();

        var max = await _db.PluginMachineConfigs
            .Where(m => m.PluginId == id)
            .Select(m => (int?)m.MachineNo)
            .MaxAsync();

        var next = (max ?? 0) + 1;

        var machine = new PluginMachineConfig
        {
            PluginId = id,
            MachineNo = next,
            Enabled = true
        };

        _db.PluginMachineConfigs.Add(machine);
        await _db.SaveChangesAsync(); // serve per ottenere machine.Id

        // ✅ auto-crea i valori per tutti i template PER_MACHINE già presenti
        var templates = await _db.PluginParameterTemplates
            .Where(t => t.PluginId == id && t.Scope == "PER_MACHINE")
            .ToListAsync();

        foreach (var t in templates)
        {
            _db.PluginParameterValues.Add(new PluginParameterValue
            {
                PluginId = id,
                TemplateId = t.Id,
                MachineConfigId = machine.Id,
                Cfusr = t.DefaultCfusr,
                Cfval = t.DefaultCfval,
                ExportEnabled = t.ExportEnabledDefault,
                UpdatedAt = DateTime.UtcNow
            });
        }

        await _db.SaveChangesAsync();
        // ✅ auto-crea anche i valori DISPATCHER (se il plugin lo gestisce)
        if (plugin.ManagesDispatcher)
        {
            var dispTemplates = await _db.DispatcherParameterTemplates
                .Where(t => t.PluginId == id)
                .ToListAsync();

            foreach (var dt in dispTemplates)
            {
                _db.DispatcherParameterValues.Add(new DispatcherParameterValue
                {
                    PluginId = id,
                    MachineConfigId = machine.Id,
                    TemplateId = dt.Id,
                    CompanyCode = "A01",
                    Value = dt.DefaultValue,
                    ExportEnabled = true,
                    UpdatedAt = DateTime.UtcNow
                });
            }

            await _db.SaveChangesAsync();
        }

        return Redirect($"/plugins/{id}");
    }
    // GET /plugins/{id}/rules
    [HttpGet("{id:long}/rules")]
    public async Task<IActionResult> Rules(long id)
    {
        var plugin = await _db.Plugins.FirstOrDefaultAsync(p => p.Id == id);
        if (plugin == null) return NotFound();

        var rules = await _db.PluginRules
            .Where(r => r.PluginId == id)
            .OrderBy(r => r.RuleType)
            .ThenBy(r => r.IfParamKeySuffix)
            .ThenBy(r => r.ThenParamKeySuffix)
            .ToListAsync();
        var keySuffixes = await _db.PluginParameterTemplates
    .Where(t => t.PluginId == id)
    .Select(t => t.KeySuffix)
    .Distinct()
    .OrderBy(x => x)
    .ToListAsync();

        return View(new PluginRulesVm
        {
            PluginId = id,
            PluginName = plugin.Name,
            Rules = rules,
            NewRule = new CreateRuleVm(),
            AvailableKeySuffixes = keySuffixes
        });
    }

    // POST /plugins/{id}/rules/create
    [HttpPost("{id:long}/rules/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateRule(long id, CreateRuleVm vm)
    {
        var pluginExists = await _db.Plugins.AnyAsync(p => p.Id == id);
        if (!pluginExists) return NotFound();

        vm.RuleType = (vm.RuleType ?? "").Trim().ToUpperInvariant();
        vm.IfParamKeySuffix = (vm.IfParamKeySuffix ?? "").Trim().ToUpperInvariant();
        vm.ThenParamKeySuffix = (vm.ThenParamKeySuffix ?? "").Trim().ToUpperInvariant();
        vm.ForcedValue = string.IsNullOrWhiteSpace(vm.ForcedValue) ? null : vm.ForcedValue.Trim();
        vm.Description = string.IsNullOrWhiteSpace(vm.Description) ? "" : vm.Description.Trim();

        if (vm.RuleType != "MUTEX" && vm.RuleType != "REQUIRE" && vm.RuleType != "FORCE_VALUE")
            ModelState.AddModelError(nameof(vm.RuleType), "RuleType non valido.");

        if (string.IsNullOrWhiteSpace(vm.IfParamKeySuffix))
            ModelState.AddModelError(nameof(vm.IfParamKeySuffix), "Obbligatorio.");

        if (string.IsNullOrWhiteSpace(vm.ThenParamKeySuffix))
            ModelState.AddModelError(nameof(vm.ThenParamKeySuffix), "Obbligatorio.");

        if (vm.RuleType == "FORCE_VALUE" && vm.ForcedValue == null)
            ModelState.AddModelError(nameof(vm.ForcedValue), "Obbligatorio per FORCE_VALUE.");

        if (!ModelState.IsValid)
        {
            var plugin = await _db.Plugins.FirstOrDefaultAsync(p => p.Id == id);
            if (plugin == null) return NotFound();

            var rules = await _db.PluginRules
                .Where(r => r.PluginId == id)
                .OrderBy(r => r.RuleType)
                .ThenBy(r => r.IfParamKeySuffix)
                .ThenBy(r => r.ThenParamKeySuffix)
                .ToListAsync();

            var keySuffixes = await _db.PluginParameterTemplates
                .Where(t => t.PluginId == id)
                .Select(t => t.KeySuffix)
                .Distinct()
                .OrderBy(x => x)
                .ToListAsync();

            return View("Rules", new PluginRulesVm
            {
                PluginId = id,
                PluginName = plugin.Name,
                Rules = rules,
                NewRule = vm,
                AvailableKeySuffixes = keySuffixes
            });
        }



        // 1) Crea la regola per il plugin corrente SOLO se non esiste già
        var existsForThisPlugin = await _db.PluginRules.AnyAsync(r =>
            r.PluginId == id &&
            r.RuleType.ToUpper() == vm.RuleType.ToUpper() &&
            r.IfParamKeySuffix.ToUpper() == vm.IfParamKeySuffix.ToUpper() &&
            r.ThenParamKeySuffix.ToUpper() == vm.ThenParamKeySuffix.ToUpper() &&
            (r.ForcedValue ?? "") == (vm.ForcedValue ?? "")
        );

        if (!existsForThisPlugin)
        {
            _db.PluginRules.Add(new PluginRule
            {
                PluginId = id,
                RuleType = vm.RuleType,
                IfParamKeySuffix = vm.IfParamKeySuffix,
                ThenParamKeySuffix = vm.ThenParamKeySuffix,
                ForcedValue = vm.ForcedValue,
                Description = vm.Description ?? ""
            });
        }

        // 2) Se richiesto: crea anche la regola globale + duplica su tutti i plugin esistenti
        if (vm.AlsoCreateGlobalRule)
        {
            // 2a) Inserisce in GlobalRules (una sola volta, no duplicati)
            var existsGlobal = await _db.GlobalRules.AnyAsync(g =>
                g.RuleType.ToUpper() == vm.RuleType.ToUpper() &&
                g.IfParamKeySuffix.ToUpper() == vm.IfParamKeySuffix.ToUpper() &&
                g.ThenParamKeySuffix.ToUpper() == vm.ThenParamKeySuffix.ToUpper() &&
                (g.ForcedValue ?? "") == (vm.ForcedValue ?? "")
            );

            if (!existsGlobal)
            {
                _db.GlobalRules.Add(new GlobalRule
                {
                    RuleType = vm.RuleType,
                    IfParamKeySuffix = vm.IfParamKeySuffix,
                    ThenParamKeySuffix = vm.ThenParamKeySuffix,
                    ForcedValue = vm.ForcedValue,
                    Description = vm.Description ?? ""
                });
            }

            // 2b) Duplica su PluginRules per TUTTI i plugin esistenti (skip duplicati)
            var allPluginIds = await _db.Plugins
                .Select(p => p.Id)
                .ToListAsync();

            // quali plugin hanno già questa regola?
            var alreadyHasRule = await _db.PluginRules
                .Where(r =>
                    r.RuleType.ToUpper() == vm.RuleType.ToUpper() &&
                    r.IfParamKeySuffix.ToUpper() == vm.IfParamKeySuffix.ToUpper() &&
                    r.ThenParamKeySuffix.ToUpper() == vm.ThenParamKeySuffix.ToUpper() &&
                    (r.ForcedValue ?? "") == (vm.ForcedValue ?? "")
                )
                .Select(r => r.PluginId)
                .ToListAsync();

            var alreadySet = new HashSet<long>(alreadyHasRule);

            foreach (var pid in allPluginIds)
            {
                if (alreadySet.Contains(pid))
                    continue;

                _db.PluginRules.Add(new PluginRule
                {
                    PluginId = pid,
                    RuleType = vm.RuleType,
                    IfParamKeySuffix = vm.IfParamKeySuffix,
                    ThenParamKeySuffix = vm.ThenParamKeySuffix,
                    ForcedValue = vm.ForcedValue,
                    Description = vm.Description ?? ""
                });
            }
        }

        await _db.SaveChangesAsync();
        return Redirect($"/plugins/{id}/rules");

    }
    // GET /plugins/{id}/scenarios
    [HttpGet("{id:long}/scenarios")]
    public async Task<IActionResult> Scenarios(long id)
    {
        var plugin = await _db.Plugins.FirstOrDefaultAsync(p => p.Id == id);
        if (plugin == null) return NotFound();

        var scenarios = await _db.PluginScenarios
            .Where(s => s.PluginId == id)
            .OrderBy(s => s.Name)
            .ToListAsync();

        return View(new PluginScenariosVm
        {
            PluginId = id,
            PluginName = plugin.Name,
            Scenarios = scenarios
        });
    }

    // POST /plugins/{id}/scenarios/create
    [HttpPost("{id:long}/scenarios/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateScenario(long id, string name)
    {
        var plugin = await _db.Plugins.FirstOrDefaultAsync(p => p.Id == id);
        if (plugin == null) return NotFound();

        name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
            return Redirect($"/plugins/{id}/scenarios");

        var exists = await _db.PluginScenarios.AnyAsync(s => s.PluginId == id && s.Name.ToLower() == name.ToLower());
        if (!exists)
        {
            _db.PluginScenarios.Add(new PluginScenario { PluginId = id, Name = name });
            await _db.SaveChangesAsync();
        }

        return Redirect($"/plugins/{id}/scenarios");
    }

    // GET /plugins/{id}/scenarios/{scenarioId}
    [HttpGet("{id:long}/scenarios/{scenarioId:long}")]
    public async Task<IActionResult> ScenarioEditor(long id, long scenarioId)
    {
        var plugin = await _db.Plugins.FirstOrDefaultAsync(p => p.Id == id);
        if (plugin == null) return NotFound();

        var scenario = await _db.PluginScenarios.FirstOrDefaultAsync(s => s.Id == scenarioId && s.PluginId == id);
        if (scenario == null) return NotFound();

        // Flag già salvati
        var flags = await _db.PluginScenarioFlags
            .Where(f => f.ScenarioId == scenarioId)
            .ToListAsync();

        // Per mostrare un elenco “guidato” di flag possibili,
        // usiamo le regole del plugin: tutto ciò che compare in IF (flag) viene mostrato.
        var ruleFlags = await _db.PluginRules
            .Where(r => r.PluginId == id)
            .Select(r => r.IfParamKeySuffix)
            .Distinct()
            .ToListAsync();

        var rows = ruleFlags
            .OrderBy(x => x)
            .Select(code =>
            {
                var existing = flags.FirstOrDefault(f => f.FlagCode.ToLower() == code.ToLower());
                return new ScenarioFlagRowVm
                {
                    FlagId = existing?.Id ?? 0,
                    FlagCode = code,
                    Enabled = existing?.Enabled ?? false
                };
            }).ToList();

        return View(new ScenarioEditorVm
        {
            PluginId = id,
            PluginName = plugin.Name,
            ScenarioId = scenarioId,
            ScenarioName = scenario.Name,
            Flags = rows
        });
    }

    // POST /plugins/{id}/scenarios
    // POST /plugins/{id}/scenarios/{scenarioId}/save
    [HttpPost("{id:long}/scenarios/{scenarioId:long}/save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveScenario(long id, long scenarioId, ScenarioEditorVm vm)
    {
        var scenario = await _db.PluginScenarios
            .FirstOrDefaultAsync(s => s.Id == scenarioId && s.PluginId == id);

        if (scenario == null) return NotFound();

        // Flag esistenti
        var existing = await _db.PluginScenarioFlags
            .Where(f => f.ScenarioId == scenarioId)
            .ToListAsync();

        var byCode = existing.ToDictionary(f => f.FlagCode.ToUpperInvariant());

        foreach (var row in vm.Flags)
        {
            var code = (row.FlagCode ?? "").Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(code)) continue;

            if (byCode.TryGetValue(code, out var f))
            {
                f.Enabled = row.Enabled;
            }
            else
            {
                _db.PluginScenarioFlags.Add(new PluginScenarioFlag
                {
                    ScenarioId = scenarioId,
                    FlagCode = code,
                    Enabled = row.Enabled
                });
            }
        }

        await _db.SaveChangesAsync();
        return Redirect($"/plugins/{id}/scenarios/{scenarioId}");
    }
    // POST /plugins/{id}/scenarios/{scenarioId}/rename
    [HttpPost("{id:long}/scenarios/{scenarioId:long}/rename")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RenameScenario(long id, long scenarioId, string name)
    {
        var scenario = await _db.PluginScenarios
            .FirstOrDefaultAsync(s => s.Id == scenarioId && s.PluginId == id);

        if (scenario == null) return NotFound();

        name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name))
            return Redirect($"/plugins/{id}/scenarios");

        // evita duplicati sullo stesso plugin
        var duplicate = await _db.PluginScenarios.AnyAsync(s =>
            s.PluginId == id &&
            s.Id != scenarioId &&
            s.Name.ToLower() == name.ToLower());

        if (!duplicate)
        {
            scenario.Name = name;
            await _db.SaveChangesAsync();
        }

        return Redirect($"/plugins/{id}/scenarios");
    }

    // POST /plugins/{id}/scenarios/{scenarioId}/delete
    [HttpPost("{id:long}/scenarios/{scenarioId:long}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteScenario(long id, long scenarioId)
    {
        var scenario = await _db.PluginScenarios
            .FirstOrDefaultAsync(s => s.Id == scenarioId && s.PluginId == id);

        if (scenario == null) return NotFound();

        _db.PluginScenarios.Remove(scenario);
        await _db.SaveChangesAsync();

        return Redirect($"/plugins/{id}/scenarios");
    }

    // POST /plugins/{id}/rules/{ruleId}/delete
    [HttpPost("{id:long}/rules/{ruleId:long}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteRule(long id, long ruleId)
    {
        var rule = await _db.PluginRules.FirstOrDefaultAsync(r => r.Id == ruleId && r.PluginId == id);
        if (rule == null) return NotFound();

        _db.PluginRules.Remove(rule);
        await _db.SaveChangesAsync();

        return Redirect($"/plugins/{id}/rules");
    }
    // GET /plugins/{id}/rules/{ruleId}/edit
    [HttpGet("{id:long}/rules/{ruleId:long}/edit")]
    public async Task<IActionResult> EditRule(long id, long ruleId)
    {
        var plugin = await _db.Plugins.FirstOrDefaultAsync(p => p.Id == id);
        if (plugin == null) return NotFound();

        var rule = await _db.PluginRules.FirstOrDefaultAsync(r => r.Id == ruleId && r.PluginId == id);
        if (rule == null) return NotFound();

        return View(new EditRuleVm
        {
            PluginId = id,
            PluginName = plugin.Name,
            RuleId = rule.Id,
            RuleType = rule.RuleType,
            IfParamKeySuffix = rule.IfParamKeySuffix,
            ThenParamKeySuffix = rule.ThenParamKeySuffix,
            ForcedValue = rule.ForcedValue,
            Description = rule.Description
        });
    }

    // POST /plugins/{id}/rules/{ruleId}/edit
    [HttpPost("{id:long}/rules/{ruleId:long}/edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditRule(long id, long ruleId, EditRuleVm vm)
    {
        var rule = await _db.PluginRules.FirstOrDefaultAsync(r => r.Id == ruleId && r.PluginId == id);
        if (rule == null) return NotFound();

        vm.RuleType = (vm.RuleType ?? "").Trim().ToUpperInvariant();
        vm.IfParamKeySuffix = (vm.IfParamKeySuffix ?? "").Trim().ToUpperInvariant();
        vm.ThenParamKeySuffix = (vm.ThenParamKeySuffix ?? "").Trim().ToUpperInvariant();
        vm.ForcedValue = string.IsNullOrWhiteSpace(vm.ForcedValue) ? null : vm.ForcedValue.Trim();
        vm.Description = string.IsNullOrWhiteSpace(vm.Description) ? "" : vm.Description.Trim();

        if (vm.RuleType != "MUTEX" && vm.RuleType != "REQUIRE" && vm.RuleType != "FORCE_VALUE")
            ModelState.AddModelError(nameof(vm.RuleType), "RuleType non valido.");

        if (string.IsNullOrWhiteSpace(vm.IfParamKeySuffix))
            ModelState.AddModelError(nameof(vm.IfParamKeySuffix), "Obbligatorio.");

        if (string.IsNullOrWhiteSpace(vm.ThenParamKeySuffix))
            ModelState.AddModelError(nameof(vm.ThenParamKeySuffix), "Obbligatorio.");

        if (vm.RuleType == "FORCE_VALUE" && vm.ForcedValue == null)
            ModelState.AddModelError(nameof(vm.ForcedValue), "Obbligatorio per FORCE_VALUE.");

        if (!ModelState.IsValid)
        {
            // ricava PluginName per il titolo
            var plugin = await _db.Plugins.FirstOrDefaultAsync(p => p.Id == id);
            vm.PluginId = id;
            vm.PluginName = plugin?.Name ?? "";
            vm.RuleId = ruleId;
            return View(vm);
        }

        rule.RuleType = vm.RuleType;
        rule.IfParamKeySuffix = vm.IfParamKeySuffix;
        rule.ThenParamKeySuffix = vm.ThenParamKeySuffix;
        rule.ForcedValue = vm.ForcedValue;
        rule.Description = vm.Description ?? "";

        await _db.SaveChangesAsync();
        return Redirect($"/plugins/{id}/rules");
    }

    // POST /plugins/{id}/machines/{machineId}/delete
    [HttpPost("{id:long}/machines/{machineId:long}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteMachine(long id, long machineId)
    {
        var machine = await _db.PluginMachineConfigs
            .FirstOrDefaultAsync(m => m.Id == machineId && m.PluginId == id);

        if (machine == null) return NotFound();

        _db.PluginMachineConfigs.Remove(machine);
        await _db.SaveChangesAsync();

        return Redirect($"/plugins/{id}");
    }

    // GET /plugins/create
    [HttpGet("create")]
    public IActionResult Create()
    {
        return View(new CreatePluginVm());
    }

    // POST /plugins/create
    [HttpPost("create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreatePluginVm vm)
    {
        if (!ModelState.IsValid)
            return View(vm);

        // Validazioni base pulite
        vm.Name = vm.Name.Trim();
        vm.DisplayName = vm.DisplayName.Trim();
        vm.DispatcherCode = string.IsNullOrWhiteSpace(vm.DispatcherCode) ? null : vm.DispatcherCode.Trim();

        if (!vm.SupportsSurveys && !vm.SupportsMachineReader)
        {
            ModelState.AddModelError("", "Devi selezionare almeno un servizio (SURVEYS o MACHINEREADER).");
            return View(vm);
        }

        if (vm.ManagesDispatcher)
        {
            if (string.IsNullOrWhiteSpace(vm.DispatcherCode))
            {
                ModelState.AddModelError(nameof(vm.DispatcherCode), "Dispatcher Code obbligatorio (es. CSB) se 'Gestisce invio in macchina' è attivo.");
                return View(vm);
            }
        }

        var exists = await _db.Plugins.AnyAsync(p => p.Name.ToLower() == vm.Name.ToLower());
        if (exists)
        {
            ModelState.AddModelError(nameof(vm.Name), "Esiste già un plugin con questo Name.");
            return View(vm);
        }

        var plugin = new Plugin
        {
            Name = vm.Name.ToUpperInvariant(),
            DisplayName = vm.DisplayName,
            SupportsSurveys = vm.SupportsSurveys,
            SupportsMachineReader = vm.SupportsMachineReader,
            ManagesDispatcher = vm.ManagesDispatcher,
            DispatcherCode = vm.ManagesDispatcher ? vm.DispatcherCode!.ToUpperInvariant() : null
        };

        _db.Plugins.Add(plugin);
        await _db.SaveChangesAsync();

        return RedirectToAction(nameof(Index));
    }
}

public class CreatePluginVm
{
    public string Name { get; set; } = "";         // TRUBENDRCI
    public string DisplayName { get; set; } = "";  // Nome leggibile

    public bool SupportsSurveys { get; set; } = true;
    public bool SupportsMachineReader { get; set; } = false;

    public bool ManagesDispatcher { get; set; } = false;
    public string? DispatcherCode { get; set; }    // CSB
}
public class PluginDetailsVm
{
    public MachineLinkConfig.Models.Plugin Plugin { get; set; } = null!;
    public List<MachineLinkConfig.Models.PluginMachineConfig> Machines { get; set; } = new();
}
public class PluginTemplatesVm
{
    public MachineLinkConfig.Models.Plugin Plugin { get; set; } = null!;
    public List<MachineLinkConfig.Models.PluginParameterTemplate> Templates { get; set; } = new();
}

public class CreateTemplateVm
{
    public long PluginId { get; set; }

    // per UI: quali root mostrare
    public bool SupportsSurveys { get; set; }
    public bool SupportsMachineReader { get; set; }

    public string ServiceRoot { get; set; } = "SURVEYS_SERVICE";
    public string Scope { get; set; } = "PER_MACHINE"; // PLUGIN_GLOBAL | PER_MACHINE

    public string ParamCode { get; set; } = "";
    public string KeySuffix { get; set; } = "";
    public string? Description { get; set; }

    public string? DefaultCfusr { get; set; }
    public string? DefaultCfval { get; set; }

    public bool ExportEnabledDefault { get; set; } = true;
    public int SortOrder { get; set; } = 0;
}
public class PluginValuesVm
{
    public MachineLinkConfig.Models.Plugin Plugin { get; set; } = null!;
    public List<ValueRowVm> GlobalRows { get; set; } = new();
    public List<MachineBlockVm> Machines { get; set; } = new();
}

public class MachineBlockVm
{
    public long MachineId { get; set; }
    public int MachineNo { get; set; }
    public bool Enabled { get; set; }
    public List<ValueRowVm> Rows { get; set; } = new();
}

public class ValueRowVm
{
    public long ValueId { get; set; }
    public long TemplateId { get; set; }

    public string ServiceRoot { get; set; } = "";
    public string Scope { get; set; } = "";

    public string KeyCf { get; set; } = "";
    public string Description { get; set; } = "";

    public string? Cfusr { get; set; }
    public string? Cfval { get; set; }
    public bool ExportEnabled { get; set; }

    public int SortOrder { get; set; }
}

public class SaveValuesVm
{
    public List<SaveValueItemVm> Items { get; set; } = new();
}

public class SaveValueItemVm
{
    public long ValueId { get; set; }
    public string? Cfusr { get; set; }
    public string? Cfval { get; set; }
    public bool ExportEnabled { get; set; }
}
public class ExportVm
{
    public long PluginId { get; set; }
    public bool IncludeHeader { get; set; } = false;
    public bool IncludeDispatcher { get; set; } = false;

    // ✅ Scenario
    public long? ScenarioId { get; set; }
    public List<ScenarioPickVm> Scenarios { get; set; } = new();
}

public class ScenarioPickVm
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
}

public class DispatcherPageVm
{
    public long PluginId { get; set; }
    public string PluginName { get; set; } = "";
    public string DispatcherCode { get; set; } = "";

    public List<DispatcherTemplateVm> Templates { get; set; } = new();
    public List<DispatcherMachineVm> Machines { get; set; } = new();
}

public class DispatcherTemplateVm
{
    public long Id { get; set; }
    public string ParamCode { get; set; } = "";
    public string Description { get; set; } = "";
    public string? DefaultValue { get; set; }
    public int SortOrder { get; set; }
}

public class DispatcherMachineVm
{
    public long MachineId { get; set; }
    public int MachineNo { get; set; }
    public List<DispatcherRowVm> Rows { get; set; } = new();
}

public class DispatcherRowVm
{
    public long ValueId { get; set; }
    public long MachineConfigId { get; set; }
    public int MachineNo { get; set; }
    public long TemplateId { get; set; }

    public string ParamCode { get; set; } = "";
    public string Description { get; set; } = "";

    public string CompanyCode { get; set; } = "A01";
    public string? Value { get; set; }
    public bool ExportEnabled { get; set; } = true;
}

public class CreateDispatcherTemplateVm
{
    public long PluginId { get; set; }
    public string ParamCode { get; set; } = "";
    public string? Description { get; set; }
    public string? DefaultValue { get; set; }
    public int SortOrder { get; set; } = 0;

    public string? DefaultCompanyCode { get; set; } = "A01";
    public bool ExportEnabledDefault { get; set; } = true;
}

public class SaveDispatcherVm
{
    public List<SaveDispatcherItemVm> Items { get; set; } = new();
}

public class SaveDispatcherItemVm
{
    public long ValueId { get; set; }
    public long MachineConfigId { get; set; }
    public long TemplateId { get; set; }

    public string? CompanyCode { get; set; }
    public string? Value { get; set; }
    public bool ExportEnabled { get; set; }
}
public class PluginRulesVm
{
    public long PluginId { get; set; }
    public string PluginName { get; set; } = "";
    public List<MachineLinkConfig.Models.PluginRule> Rules { get; set; } = new();
    public CreateRuleVm NewRule { get; set; } = new();

    public List<string> AvailableKeySuffixes { get; set; } = new();
}

public class CreateRuleVm
{
    public string RuleType { get; set; } = "MUTEX"; // MUTEX | REQUIRE | FORCE_VALUE
    public string IfParamKeySuffix { get; set; } = "";
    public string ThenParamKeySuffix { get; set; } = "";
    public string? ForcedValue { get; set; }
    public string? Description { get; set; }
    public bool AlsoCreateGlobalRule { get; set; } = false;
}
public class PluginScenariosVm
{
    public long PluginId { get; set; }
    public string PluginName { get; set; } = "";
    public List<MachineLinkConfig.Models.PluginScenario> Scenarios { get; set; } = new();
}

public class ScenarioEditorVm
{
    public long PluginId { get; set; }
    public string PluginName { get; set; } = "";

    public long ScenarioId { get; set; }
    public string ScenarioName { get; set; } = "";

    public List<ScenarioFlagRowVm> Flags { get; set; } = new();
}

public class ScenarioFlagRowVm
{
    public long FlagId { get; set; }
    public string FlagCode { get; set; } = "";
    public bool Enabled { get; set; }
}
public class EditRuleVm
{
    public long PluginId { get; set; }
    public string PluginName { get; set; } = "";
    public long RuleId { get; set; }

    public string RuleType { get; set; } = "MUTEX";
    public string IfParamKeySuffix { get; set; } = "";
    public string ThenParamKeySuffix { get; set; } = "";
    public string? ForcedValue { get; set; }
    public string? Description { get; set; }
}
public class EditTemplateVm
{
    public long PluginId { get; set; }
    public string PluginName { get; set; } = "";

    public long TemplateId { get; set; }

    public bool SupportsSurveys { get; set; }
    public bool SupportsMachineReader { get; set; }

    public string ServiceRoot { get; set; } = "SURVEYS_SERVICE";
    public string Scope { get; set; } = "PER_MACHINE";

    public string ParamCode { get; set; } = "";
    public string KeySuffix { get; set; } = "";
    public string Description { get; set; } = "";

    public string? DefaultCfusr { get; set; }
    public string? DefaultCfval { get; set; }

    public bool ExportEnabledDefault { get; set; } = true;
    public int SortOrder { get; set; } = 0;
}
public class ImportPluginsVm
{
    public string RawText { get; set; } = "";
    public bool EnableExportOnImported { get; set; } = true;
    public ImportResultVm? Result { get; set; }
}

public class ImportResultVm
{
    public int PluginsCreated { get; set; }
    public int PluginsUpdated { get; set; }
    public int MachinesCreated { get; set; }

    public int TemplatesCreated { get; set; }
    public int ValuesCreated { get; set; }
    public int ValuesUpdated { get; set; }

    public int DispatcherTemplatesCreated { get; set; }
    public int DispatcherValuesCreated { get; set; }
    public int DispatcherValuesUpdated { get; set; }

    public int SkippedLines { get; set; }
    public List<string> Messages { get; set; } = new();
}

