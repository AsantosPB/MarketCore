using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using MarketCore.WPF.Models.PregaoVivaVoz;

namespace MarketCore.WPF.Services.PregaoVivaVoz
{
    /// <summary>
    /// Gerencia persistência JSON das configurações do Pregão Viva Voz.
    /// Salva e carrega automaticamente do diretório Config/PregaoVivaVoz/
    /// </summary>
    public class ConfigPersistenceService
    {
        private readonly string _diretorioConfig;
        private readonly JsonSerializerOptions _jsonOptions;
        
        public ConfigPersistenceService()
        {
            // Diretório baseado na pasta de execução
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _diretorioConfig = Path.Combine(baseDir, "Config", "PregaoVivaVoz");
            
            // Garante que o diretório exista
            if (!Directory.Exists(_diretorioConfig))
            {
                Directory.CreateDirectory(_diretorioConfig);
            }
            
            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
        }
        
        // ============ CATÁLOGO DE PLAYERS ============
        
        /// <summary>
        /// Carrega o catálogo com os 28 players.
        /// </summary>
        public async Task<List<PlayerConfig>> CarregarPlayersAsync()
        {
            var caminho = Path.Combine(_diretorioConfig, "players_catalogo.json");
            
            if (!File.Exists(caminho))
            {
                Console.WriteLine($"[ConfigPersistence] Arquivo não encontrado: {caminho}");
                return new List<PlayerConfig>();
            }
            
            try
            {
                var json = await File.ReadAllTextAsync(caminho);
                var doc = JsonDocument.Parse(json);
                
                if (!doc.RootElement.TryGetProperty("players", out var playersElement))
                {
                    return new List<PlayerConfig>();
                }
                
                var players = JsonSerializer.Deserialize<List<PlayerConfig>>(
                    playersElement.GetRawText(), 
                    _jsonOptions);
                
                Console.WriteLine($"[ConfigPersistence] {players?.Count ?? 0} players carregados");
                return players ?? new List<PlayerConfig>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ConfigPersistence] Erro ao carregar players: {ex.Message}");
                return new List<PlayerConfig>();
            }
        }
        
        /// <summary>
        /// Salva o catálogo com todas as configurações dos players.
        /// </summary>
        public async Task SalvarPlayersAsync(List<PlayerConfig> players)
        {
            var caminho = Path.Combine(_diretorioConfig, "players_catalogo.json");
            
            try
            {
                var wrapper = new
                {
                    versao = "1.0.0",
                    atualizado_em = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    descricao = "Catálogo das 28 corretoras monitoradas no Pregão Viva Voz",
                    players = players
                };
                
                var json = JsonSerializer.Serialize(wrapper, _jsonOptions);
                await File.WriteAllTextAsync(caminho, json);
                
                Console.WriteLine($"[ConfigPersistence] {players.Count} players salvos");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ConfigPersistence] Erro ao salvar players: {ex.Message}");
            }
        }
        
        // ============ CONFIG RAJADA GLOBAL ============
        
        public async Task<ConfigRajadaGlobal> CarregarConfigRajadaAsync()
        {
            var caminho = Path.Combine(_diretorioConfig, "config_rajada_global.json");
            
            if (!File.Exists(caminho))
            {
                return new ConfigRajadaGlobal();
            }
            
            try
            {
                var json = await File.ReadAllTextAsync(caminho);
                var config = JsonSerializer.Deserialize<ConfigRajadaGlobal>(json, _jsonOptions);
                return config ?? new ConfigRajadaGlobal();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ConfigPersistence] Erro ao carregar rajada: {ex.Message}");
                return new ConfigRajadaGlobal();
            }
        }
        
        public async Task SalvarConfigRajadaAsync(ConfigRajadaGlobal config)
        {
            var caminho = Path.Combine(_diretorioConfig, "config_rajada_global.json");
            
            try
            {
                var wrapper = new
                {
                    versao = "1.0.0",
                    descricao = "Parâmetros globais do detector de rajada",
                    sequenciaMinima = config.SequenciaMinima,
                    janelaMilissegundos = config.JanelaMilissegundos,
                    volumeMinimo = config.VolumeMinimo,
                    silencioParouMilissegundos = config.SilencioParouMilissegundos
                };
                
                var json = JsonSerializer.Serialize(wrapper, _jsonOptions);
                await File.WriteAllTextAsync(caminho, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ConfigPersistence] Erro ao salvar rajada: {ex.Message}");
            }
        }
        
        // ============ FRASES A GRAVAR ============
        
        public async Task<List<FraseGravacao>> CarregarFrasesAsync()
        {
            var caminho = Path.Combine(_diretorioConfig, "frases_a_gravar.json");
            
            if (!File.Exists(caminho))
            {
                return new List<FraseGravacao>();
            }
            
            try
            {
                var json = await File.ReadAllTextAsync(caminho);
                var doc = JsonDocument.Parse(json);
                
                if (!doc.RootElement.TryGetProperty("frases", out var frasesElement))
                {
                    return new List<FraseGravacao>();
                }
                
                var opts = new JsonSerializerOptions(_jsonOptions);
                opts.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
                
                var frases = JsonSerializer.Deserialize<List<FraseGravacao>>(
                    frasesElement.GetRawText(), 
                    opts);
                
                Console.WriteLine($"[ConfigPersistence] {frases?.Count ?? 0} frases carregadas");
                return frases ?? new List<FraseGravacao>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ConfigPersistence] Erro ao carregar frases: {ex.Message}");
                return new List<FraseGravacao>();
            }
        }
        
        /// <summary>
        /// Salva o estado atualizado das frases (gravado/duração).
        /// </summary>
        public async Task SalvarEstadoFrasesAsync(List<FraseGravacao> frases)
        {
            var caminho = Path.Combine(_diretorioConfig, "frases_estado.json");
            
            try
            {
                var estado = new List<object>();
                foreach (var f in frases)
                {
                    if (f.Gravado)
                    {
                        estado.Add(new
                        {
                            id = f.Id,
                            gravado = f.Gravado,
                            duracaoSegundos = f.DuracaoSegundos,
                            dataGravacao = f.DataGravacao,
                            temErro = f.TemErro,
                            mensagemErro = f.MensagemErro
                        });
                    }
                }
                
                var json = JsonSerializer.Serialize(estado, _jsonOptions);
                await File.WriteAllTextAsync(caminho, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ConfigPersistence] Erro ao salvar estado: {ex.Message}");
            }
        }
        
        // ============ HELPERS ============
        
        /// <summary>
        /// Retorna o diretório base de áudios do Pregão Viva Voz.
        /// </summary>
        public string GetDiretorioAudio()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var dir = Path.Combine(baseDir, "Audio", "PregaoVivaVoz");
            
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            
            return dir;
        }
        
        /// <summary>
        /// Retorna o diretório de config.
        /// </summary>
        public string GetDiretorioConfig() => _diretorioConfig;
    }
}
