using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using GameReaderCommon;
using SimHub.Plugins;

namespace SimSetups
{
    [PluginDescription("Sim Setups Telemetry Plugin (v2.5.3 fix definitivo - fallback LastLapTime)")]
    [PluginAuthor("SimSetups")]
    [PluginName("Sim Setups Telemetry")]
    public class TelemetryPlugin : IPlugin, IDataPlugin
    {
        private const string VERSION = "2.5.3";
        private const int TRACE_HZ = 10;

        // ============================================================
        // v2.5.0 FEATURE FLAGS
        // Se algum fix der problema em teste, basta setar pra false
        // e recompilar. Os 4 sao independentes entre si.
        // ============================================================
        private const bool FIX_FINAL_LAP_FLUSH      = true;  // grava ultima volta no End()/troca de sessao
        private const bool FIX_STRICT_OPPONENT      = true;  // online: exige playerOpp e nao "conserta" lap_time
        private const bool FIX_LAP_JUMP_GUARD       = true;  // descarta saltos > 1 em CompletedLaps
        private const bool FIX_SETTLE_WINDOW        = true;  // espera 200ms apos detectar volta nova
        private const bool FIX_LASTLAPTIME_FALLBACK = true;  // v2.5.3: detecta volta quando LastLapTime muda mas CompletedLaps trava
        private const int  SETTLE_WINDOW_MS         = 200;

        private string _apiToken;
        private string _endpoint;
        private string _logFilePath;            // v2.5.1 — arquivo de log proprio (Console.WriteLine nao aparece no log do SimHub)
        private string _sessionId;
        private string _currentTrack;
        private string _sessionType;
        private int _lastCompletedLaps = -1;
        private int _totalLaps;
        private bool _sessionActive;
        private DateTime _lastTraceSample = DateTime.MinValue;
        private List<Dictionary<string, object>> _traceBuffer = new List<Dictionary<string, object>>();
        private int _stintNumber;
        private string _currentCompound;
        private int _stintStartLap;
        private double[] _stintStartWear = new double[4];
        private int _tireAgeLaps;
        private double _lastWearAvg = -1.0;
        private int _lastSentLapNumber = -1;
        private string _sessionUid;

        // === v2.4.1 anti-spectator fields ===
        private int _playerThrottleSamples;
        private int _playerBrakeSamples;
        private int _playerSpeedSamples;
        private int _totalSamplesThisLap;
        private int _lastPlayerOpponentLap = -1;
        private double _lastPlayerOpponentLastLap = 0.0;

        // === v2.5.0 FIX 1 — snapshot da ultima volta vista para flush no End/troca de sessao ===
        // Capturamos os campos primitivos (nao referencia direta ao SDB) pra evitar problemas
        // se o SimHub mutar/descartar o objeto depois que End() for chamado.
        private bool _finalLapFlushed;          // evita enviar a mesma volta duas vezes
        private FinalLapSnapshot _lastSnapshot; // ultimo estado completo capturado em DataUpdate

        // === v2.5.0 FIX 3 — guard contra saltos suspeitos em CompletedLaps ===
        // Quando o SDB troca de "carro foco" (espectador), CompletedLaps pula varias unidades.
        // Quando detectamos isso, ressincronizamos o estado e nao enviamos.

        // === v2.5.0 FIX 4 — janela de settle ===
        // Nao envia a volta na primeira deteccao. Espera SETTLE_WINDOW_MS pra:
        //  1. dar tempo do LastLapTime estabilizar (as vezes vem zero no primeiro update apos cruzar)
        //  2. validar contra o playerOpp.LastLapTime atualizado
        private int _pendingLapNumber = -1;
        private DateTime _pendingLapDetectedAt = DateTime.MinValue;

        // ============================================================
        // v2.5.3 FIX 5 — FALLBACK PARA ULTIMA VOLTA DE CORRIDA PLANEJADA
        // ============================================================
        // Bug descoberto via log v2.5.2: na ULTIMA volta de uma corrida planejada
        // do F1 25, o jogo trava CompletedLaps em N-1 e atualiza apenas LastLapTime
        // para o tempo da volta finalizada. O detector tradicional (que exige
        // CompletedLaps > _lastSentLapNumber) nao dispara.
        //
        // Este fallback detecta o cenario: LastLapTime mudou para valor novo valido
        // E CompletedLaps continua igual a _lastSentLapNumber. Trata como volta
        // _lastSentLapNumber + 1.
        //
        // Esse era o bug historico que assombrava o projeto desde a v2.4.0.
        // ============================================================
        private double _lastSentLapTime = 0.0;        // tempo (s) da volta enviada por ultimo
        private double _lastFallbackTriedTime = 0.0;  // ultimo LastLapTime que tentamos fallback (evita re-trigger)

        // ============================================================
        // v2.5.2 — campos de diagnostico amplo
        // Registram estado interno periodicamente pra diagnosticar bug
        // intermitente da ultima volta (DLL parando de detectar volta nova)
        // ============================================================
        private DateTime _lastHeartbeatLog = DateTime.MinValue;
        private DateTime _lastDataUpdateAt = DateTime.MinValue;
        private long _dataUpdateCallsTotal;       // contador total de chamadas
        private long _dataUpdateCallsSinceLastLog; // contador desde ultimo heartbeat
        private int _lastSeenCompletedLaps = -1;  // pra detectar TODA mudança em CompletedLaps
        private double _lastSeenLastLapTime = -1.0; // pra detectar TODA mudança em LastLapTime
        private bool _lastSeenGamePaused = false;

        public PluginManager PluginManager { get; set; }

        // ============================================================
        // v2.5.0 FIX 1 — snapshot completa do estado da ultima volta
        // ============================================================
        private class FinalLapSnapshot
        {
            public int CompletedLaps;
            public double LastLapTimeSec;
            public double S1Sec, S2Sec, S3Sec;
            public double SpeedKmh;
            public double TyreWearFL, TyreWearFR, TyreWearRL, TyreWearRR;
            public double Fuel;
            public int Position;
            public string Compound;
            public double RoadTemp, AirTemp;
            public List<Dictionary<string, object>> TraceCopy;
            public int OppLap;
            public double OppLastLap;
            public int InputThrottleSamples, InputSpeedSamples, TotalSamples;
            public bool IsOnline;
            public bool HasPlayerOpponent;
        }

        public void Init(PluginManager pluginManager)
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            // v2.5.1 — inicializa logger em arquivo ANTES de qualquer Log()
            _logFilePath = Path.Combine(baseDirectory, "SimSetups_Plugin.log");
            try
            {
                File.AppendAllText(_logFilePath,
                    Environment.NewLine +
                    "=========================================" + Environment.NewLine +
                    "=== Plugin v" + VERSION + " started at " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===" + Environment.NewLine +
                    "=========================================" + Environment.NewLine);
            }
            catch { }

            string path = Path.Combine(baseDirectory, "SimSetups_Token.txt");
            if (File.Exists(path)) _apiToken = File.ReadAllText(path).Trim();
            if (string.IsNullOrEmpty(_apiToken))
            {
                Log("ERROR: SimSetups_Token.txt not found or empty in " + baseDirectory);
                return;
            }
            _endpoint = "https://glznhmlhlkxupejcfrgl.supabase.co/functions/v1/simhub-telemetry-ingest";
            Log("Plugin v" + VERSION + " initialized. Token loaded.");
            try
            {
                var ping = new Dictionary<string, object> {
                    { "pilot_token", _apiToken },
                    { "action", "ping" },
                    { "plugin_version", VERSION }
                };
                SendData(ping);
                Log("Ping sent successfully.");
            }
            catch (Exception ex) { Log("Ping failed: " + ex.Message); }
        }

        public void End(PluginManager pluginManager)
        {
            // v2.5.2 — log de chamada do End (raro, ajuda diagnostico)
            Log("End() called. _sessionActive=" + _sessionActive +
                " _sessionId=" + (_sessionId ?? "null") +
                " _lastSentLapNumber=" + _lastSentLapNumber +
                " _lastCompletedLaps=" + _lastCompletedLaps +
                " dataUpdateCalls=" + _dataUpdateCallsTotal);

            if (!_sessionActive || string.IsNullOrEmpty(_sessionId)) return;

            // === v2.5.0 FIX 1 — flush da ultima volta antes de fechar a sessao ===
            // Se houve uma volta detectada que nao chegou a ser enviada (porque o usuario
            // saiu do jogo ou a sessao terminou antes do proximo DataUpdate), enviamos agora
            // usando a snapshot capturada no ultimo DataUpdate.
            if (FIX_FINAL_LAP_FLUSH)
            {
                TryFlushFinalLap("End");
            }

            try
            {
                var msg = new Dictionary<string, object> {
                    { "pilot_token", _apiToken },
                    { "session_id", _sessionId },
                    { "action", "end_session" },
                    { "completed_laps", _lastCompletedLaps },
                    { "total_laps", _totalLaps }
                };
                SendData(msg);
                Log("Session ended: " + _sessionId);
            }
            catch (Exception ex) { Log("End session error: " + ex.Message); }
        }

        // ============================================================
        // v2.5.0 FIX 1 — flush da ultima volta usando snapshot
        // Chamado em 2 cenarios:
        //  1. End() do plugin (SimHub fechando)
        //  2. Mudanca de pista/sessao detectada em DataUpdate (antes do end_session)
        // ============================================================
        private void TryFlushFinalLap(string trigger)
        {
            try
            {
                if (_finalLapFlushed) return;
                if (_lastSnapshot == null) return;
                if (_lastSnapshot.CompletedLaps <= _lastSentLapNumber) return;
                if (_lastSnapshot.LastLapTimeSec < 5.0 || _lastSnapshot.LastLapTimeSec > 600.0) return;

                Log("FINAL LAP FLUSH (" + trigger + ") - lap=" + _lastSnapshot.CompletedLaps + " time=" + _lastSnapshot.LastLapTimeSec.ToString("F3"));

                // Validacao basica anti-spectator no flush tambem (mais permissiva que SendLap normal,
                // pq se o usuario terminou a corrida pode estar parado nos boxes)
                if (_lastSnapshot.IsOnline && !_lastSnapshot.HasPlayerOpponent)
                {
                    Log("FINAL LAP FLUSH REJECTED: online sem playerOpp (suspeita de spectator)");
                    _finalLapFlushed = true;
                    return;
                }

                if (_lastSnapshot.HasPlayerOpponent && _lastSnapshot.OppLastLap > 5.0 &&
                    Math.Abs(_lastSnapshot.OppLastLap - _lastSnapshot.LastLapTimeSec) > 0.5)
                {
                    Log("FINAL LAP FLUSH REJECTED: lap_time divergente do playerOpp");
                    _finalLapFlushed = true;
                    return;
                }

                var lap = new Dictionary<string, object> {
                    { "lap_number", _lastSnapshot.CompletedLaps },
                    { "lap_time", _lastSnapshot.LastLapTimeSec },
                    { "sector1_time", _lastSnapshot.S1Sec > 0 ? (object)_lastSnapshot.S1Sec : null },
                    { "sector2_time", _lastSnapshot.S2Sec > 0 ? (object)_lastSnapshot.S2Sec : null },
                    { "sector3_time", _lastSnapshot.S3Sec > 0 ? (object)_lastSnapshot.S3Sec : null },
                    { "top_speed", _lastSnapshot.SpeedKmh },
                    { "tire_compound", _lastSnapshot.Compound },
                    { "tire_age_laps", _tireAgeLaps },
                    { "tire_wear_fl", _lastSnapshot.TyreWearFL },
                    { "tire_wear_fr", _lastSnapshot.TyreWearFR },
                    { "tire_wear_rl", _lastSnapshot.TyreWearRL },
                    { "tire_wear_rr", _lastSnapshot.TyreWearRR },
                    { "fuel_remaining", _lastSnapshot.Fuel },
                    { "ers_stored", 0.0 },
                    { "position", _lastSnapshot.Position },
                    { "is_valid", true },
                    { "stint_number", _stintNumber },
                    { "stint_compound", _currentCompound ?? _lastSnapshot.Compound },
                    { "stint_start_lap", _stintStartLap },
                    { "stint_start_wear", _stintStartWear },
                    { "trace_data", _lastSnapshot.TraceCopy ?? new List<Dictionary<string, object>>() },
                    { "input_throttle_ratio", _lastSnapshot.TotalSamples > 0 ? (double)_lastSnapshot.InputThrottleSamples / _lastSnapshot.TotalSamples : 0.0 },
                    { "input_speed_ratio",    _lastSnapshot.TotalSamples > 0 ? (double)_lastSnapshot.InputSpeedSamples    / _lastSnapshot.TotalSamples : 0.0 },
                    { "samples_in_lap", _lastSnapshot.TotalSamples },
                    { "is_final_flush", true }  // marca pro backend saber que veio do flush
                };

                var sessionInfo = new Dictionary<string, object> {
                    { "track_name", _currentTrack ?? "" },
                    { "session_type", _sessionType ?? "race" },
                    { "weather", "" },
                    { "track_temperature", _lastSnapshot.RoadTemp },
                    { "air_temperature", _lastSnapshot.AirTemp },
                    { "session_uid", _sessionUid }
                };

                var msg = new Dictionary<string, object> {
                    { "pilot_token", _apiToken },
                    { "action", "lap" },
                    { "lap", lap },
                    { "session_info", sessionInfo },
                    { "completed_laps", _lastSnapshot.CompletedLaps },
                    { "total_laps", _totalLaps },
                    { "plugin_version", VERSION }
                };
                if (!string.IsNullOrEmpty(_sessionId)) msg["session_id"] = _sessionId;

                SendData(msg);
                _lastSentLapNumber = _lastSnapshot.CompletedLaps;
                _finalLapFlushed = true;
                Log("FINAL LAP FLUSHED OK - lap " + _lastSnapshot.CompletedLaps);
            }
            catch (Exception ex)
            {
                Log("Final lap flush error: " + ex.Message);
            }
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            // v2.5.2 — contar TODA chamada, mesmo que falhe na guard inicial
            _dataUpdateCallsTotal++;
            _dataUpdateCallsSinceLastLog++;
            _lastDataUpdateAt = DateTime.Now;

            if (string.IsNullOrEmpty(_apiToken) || data == null || data.NewData == null) return;
            StatusDataBase newData = data.NewData;
            try
            {
                // ============================================================
                // v2.5.2 DIAGNOSTICO 1 — logar TODA mudança em CompletedLaps e LastLapTime
                // (mesmo que nao dispare detect — ajuda a ver se DataUpdate continua chegando)
                // ============================================================
                int curCompletedLaps = newData.CompletedLaps;
                double curLastLapTime = newData.LastLapTime.TotalSeconds;
                bool curGamePaused = SafeGetGamePaused(data);

                if (curCompletedLaps != _lastSeenCompletedLaps)
                {
                    Log("STATE_CHG completedLaps: " + _lastSeenCompletedLaps + " -> " + curCompletedLaps +
                        " | lastLapTime=" + curLastLapTime.ToString("F3") +
                        " | _lastSent=" + _lastSentLapNumber +
                        " | _lastCompleted=" + _lastCompletedLaps +
                        " | _pending=" + _pendingLapNumber);
                    _lastSeenCompletedLaps = curCompletedLaps;
                }
                if (Math.Abs(curLastLapTime - _lastSeenLastLapTime) > 0.001)
                {
                    Log("STATE_CHG lastLapTime: " + _lastSeenLastLapTime.ToString("F3") + " -> " + curLastLapTime.ToString("F3") +
                        " | completedLaps=" + curCompletedLaps);
                    _lastSeenLastLapTime = curLastLapTime;
                }
                if (curGamePaused != _lastSeenGamePaused)
                {
                    Log("STATE_CHG gamePaused: " + _lastSeenGamePaused + " -> " + curGamePaused +
                        " | completedLaps=" + curCompletedLaps + " | lastLapTime=" + curLastLapTime.ToString("F3"));
                    _lastSeenGamePaused = curGamePaused;
                }

                // ============================================================
                // v2.5.2 DIAGNOSTICO 2 — heartbeat a cada 5s
                // Confirma que DataUpdate continua sendo chamado
                // ============================================================
                if ((DateTime.Now - _lastHeartbeatLog).TotalSeconds >= 5.0)
                {
                    Log("HEARTBEAT updates=" + _dataUpdateCallsSinceLastLog +
                        " total=" + _dataUpdateCallsTotal +
                        " | completedLaps=" + curCompletedLaps +
                        " | lastLapTime=" + curLastLapTime.ToString("F3") +
                        " | _lastSent=" + _lastSentLapNumber +
                        " | _lastCompleted=" + _lastCompletedLaps +
                        " | _pending=" + _pendingLapNumber +
                        " | gamePaused=" + curGamePaused +
                        " | session=" + (_sessionType ?? "none"));
                    _lastHeartbeatLog = DateTime.Now;
                    _dataUpdateCallsSinceLastLog = 0;
                }

                string track = newData.TrackName ?? "";
                string sessionType = newData.SessionTypeName ?? "race";
                string compound = ExtractCompound(data);

                // v2.4.1 — get the player's own opponent record (canonical source of truth)
                Opponent playerOpp = FindPlayerOpponent(newData);

                if (_currentTrack != track || _sessionType != sessionType)
                {
                    if (_sessionActive && !string.IsNullOrEmpty(_sessionId))
                    {
                        // === v2.5.0 FIX 1 — flush antes de fechar sessao ===
                        if (FIX_FINAL_LAP_FLUSH)
                        {
                            TryFlushFinalLap("SessionChange");
                        }

                        try
                        {
                            var endMsg = new Dictionary<string, object> {
                                { "pilot_token", _apiToken },
                                { "session_id", _sessionId },
                                { "action", "end_session" },
                                { "completed_laps", _lastCompletedLaps },
                                { "total_laps", _totalLaps }
                            };
                            SendData(endMsg);
                        }
                        catch { }
                    }
                    _currentTrack = track;
                    _sessionType = sessionType;
                    _sessionUid = Guid.NewGuid().ToString();
                    _sessionId = null;
                    _sessionActive = true;
                    _lastCompletedLaps = -1;
                    _totalLaps = 0;
                    _stintNumber = 0;
                    _currentCompound = null;
                    _stintStartLap = 0;
                    _tireAgeLaps = 0;
                    _lastWearAvg = -1.0;
                    _lastSentLapNumber = -1;
                    _traceBuffer.Clear();
                    ResetLapInputCounters();
                    // === v2.5.0 — reset de estado novo ===
                    _finalLapFlushed = false;
                    _lastSnapshot = null;
                    _pendingLapNumber = -1;
                    _pendingLapDetectedAt = DateTime.MinValue;
                    Log("New session detected: " + track + " / " + sessionType + " / compound: " + compound);
                }

                if (compound != null && compound != "unknown" && compound != _currentCompound)
                {
                    _stintNumber++;
                    string prev = _currentCompound ?? "unknown";
                    _currentCompound = compound;
                    _stintStartLap = newData.CompletedLaps;
                    _tireAgeLaps = 0;
                    _stintStartWear[0] = SafeDouble(newData.TyreWearFrontLeft);
                    _stintStartWear[1] = SafeDouble(newData.TyreWearFrontRight);
                    _stintStartWear[2] = SafeDouble(newData.TyreWearRearLeft);
                    _stintStartWear[3] = SafeDouble(newData.TyreWearRearRight);
                    Log("Compound changed: " + prev + " -> " + compound + " (stint " + _stintNumber + ")");
                }

                int sampleIntervalMs = 100;
                if ((DateTime.Now - _lastTraceSample).TotalMilliseconds >= (double)sampleIntervalMs)
                {
                    _lastTraceSample = DateTime.Now;
                    string g = newData.Gear;
                    int gear = 0;
                    if (g == "N") gear = 0;
                    else if (g == "R") gear = -1;
                    else int.TryParse(g, out gear);

                    double thr = newData.Throttle * 100.0;
                    double brk = newData.Brake * 100.0;
                    double spd = newData.SpeedKmh;

                    var sample = new Dictionary<string, object> {
                        { "t", Math.Round(newData.CurrentLapTime.TotalSeconds, 3) },
                        { "s", Math.Round(spd, 1) },
                        { "th", Math.Round(thr, 1) },
                        { "b", Math.Round(brk, 1) },
                        { "g", gear },
                        { "st", 0.0 },
                        { "drs", newData.DRSEnabled == 1 }
                    };
                    if (newData.AccelerationSway.HasValue) sample["lg"] = Math.Round(newData.AccelerationSway.Value, 3);
                    if (newData.AccelerationSurge.HasValue) sample["longg"] = Math.Round(newData.AccelerationSurge.Value, 3);
                    TryAddRawFields(data, sample);
                    _traceBuffer.Add(sample);

                    // v2.4.1 — count input activity to detect spectator (no driver inputs)
                    _totalSamplesThisLap++;
                    if (thr > 1.0) _playerThrottleSamples++;
                    if (brk > 1.0) _playerBrakeSamples++;
                    if (spd > 5.0) _playerSpeedSamples++;
                }

                // Track player's own opponent lap progression (used to validate)
                if (playerOpp != null)
                {
                    int oppLap = SafeInt(GetMember(playerOpp, "CurrentLap"));
                    double oppLastLap = ParseLapTimeSeconds(GetMember(playerOpp, "LastLapTime"));
                    if (oppLap > 0) _lastPlayerOpponentLap = oppLap;
                    if (oppLastLap > 0) _lastPlayerOpponentLastLap = oppLastLap;
                }

                // ============================================================
                // v2.5.0 FIX 3 — guard contra saltos suspeitos em CompletedLaps
                // Quando o SDB troca de "carro foco" (ex: spectator), CompletedLaps
                // pode pular varias unidades. Detectamos e ressincronizamos sem enviar.
                // ============================================================
                bool suspiciousJump = false;
                if (FIX_LAP_JUMP_GUARD && _lastCompletedLaps >= 0 && newData.CompletedLaps > _lastCompletedLaps)
                {
                    int delta = newData.CompletedLaps - _lastCompletedLaps;
                    if (delta > 1)
                    {
                        Log("LAP JUMP GUARD: pulou de " + _lastCompletedLaps + " para " + newData.CompletedLaps + " (delta=" + delta + ") - provavel troca de carro foco, ressincronizando sem enviar");
                        _lastSentLapNumber = newData.CompletedLaps;  // ja "considera enviada" pra nao tentar
                        _pendingLapNumber = -1;
                        suspiciousJump = true;
                    }
                }

                // ============================================================
                // v2.5.3 FIX 5 — FALLBACK: LastLapTime mudou mas CompletedLaps travou
                // Cenario classico do F1 25 ULTIMA volta de corrida planejada.
                // ============================================================
                bool fallbackTrigger = false;
                int targetLapNumber = newData.CompletedLaps;  // padrao: usa CompletedLaps
                double curLastLapTimeSec = newData.LastLapTime.TotalSeconds;

                if (FIX_LASTLAPTIME_FALLBACK &&
                    !suspiciousJump &&
                    _lastSentLapNumber > 0 &&                                          // ja enviou pelo menos 1 volta
                    curLastLapTimeSec > 5.0 &&                                         // LastLapTime valido
                    Math.Abs(curLastLapTimeSec - _lastSentLapTime) > 0.05 &&           // != da ultima enviada (>50ms)
                    Math.Abs(curLastLapTimeSec - _lastFallbackTriedTime) > 0.05 &&     // ainda nao tentamos fallback pra esse valor
                    newData.CompletedLaps == _lastSentLapNumber)                       // CompletedLaps NAO incrementou
                {
                    targetLapNumber = _lastSentLapNumber + 1;
                    fallbackTrigger = true;
                    _lastFallbackTriedTime = curLastLapTimeSec;
                    Log("FALLBACK LASTLAP TRIGGER: completedLaps=" + newData.CompletedLaps +
                        " (travado em _lastSent=" + _lastSentLapNumber + ")" +
                        " lastLapTime mudou de " + _lastSentLapTime.ToString("F3") +
                        " para " + curLastLapTimeSec.ToString("F3") +
                        " - tratando como volta " + targetLapNumber);
                }

                // ============================================================
                // v2.5.0 FIX 4 — janela de settle antes de enviar volta nova
                // v2.5.1 EVOLUCAO: settle "inteligente". Se LastLapTime ja vem
                //   valido (>5s) no primeiro detect, envia IMEDIATO. Settle so
                //   ativa se LastLapTime ainda eh zero (raro no F1 25, mas pode
                //   acontecer em outros jogos).
                //
                // POR QUE: o F1 25 entra em animacao cinematografica logo apos
                //   a ultima volta da corrida (~30ms depois) e troca a camera
                //   pro vencedor. Esperar 200ms = perder a volta.
                //
                // O SimHub gera "New valid lap detected" no GameReaderCommon
                //   exatamente no primeiro update apos cruzar a linha — entao
                //   o LastLapTime ja vem certo nesse momento.
                // ============================================================
                if (fallbackTrigger ||
                    (!suspiciousJump &&
                     newData.CompletedLaps > 0 &&
                     newData.CompletedLaps > _lastSentLapNumber &&
                     (newData.CompletedLaps > _lastCompletedLaps || newData.LastLapTime.TotalSeconds > 5.0)))
                {
                    bool readyToSend = true;
                    bool lapTimeAlreadyValid = newData.LastLapTime.TotalSeconds > 5.0;

                    if (FIX_SETTLE_WINDOW)
                    {
                        if (_pendingLapNumber != targetLapNumber)
                        {
                            // primeira deteccao — arma a janela e captura snapshot AGORA
                            _pendingLapNumber = targetLapNumber;
                            _pendingLapDetectedAt = DateTime.Now;
                            UpdateFinalLapSnapshot(newData, data, compound, playerOpp);
                            Log("LAP DETECT lap=" + targetLapNumber + " lastLapTime=" + newData.LastLapTime.TotalSeconds.ToString("F3") + " validNow=" + lapTimeAlreadyValid + " fallback=" + fallbackTrigger);

                            if (!lapTimeAlreadyValid) readyToSend = false;
                        }
                        else if (!lapTimeAlreadyValid)
                        {
                            double elapsedMs = (DateTime.Now - _pendingLapDetectedAt).TotalMilliseconds;
                            if (elapsedMs < SETTLE_WINDOW_MS)
                            {
                                readyToSend = false;
                            }
                        }
                    }

                    if (readyToSend)
                    {
                        // v2.5.3: passa lapNumberOverride se for fallback
                        int overrideLap = fallbackTrigger ? targetLapNumber : -1;
                        if (SendLap(newData, data, compound, playerOpp, overrideLap))
                        {
                            _lastSentLapNumber = targetLapNumber;
                            _lastSentLapTime = newData.LastLapTime.TotalSeconds;  // v2.5.3: rastreia tempo
                            _tireAgeLaps++;
                            ResetLapInputCounters();
                        }
                        _pendingLapNumber = -1;
                    }
                }

                // ============================================================
                // v2.5.0 FIX 1 — atualizar snapshot a cada DataUpdate
                // Captura o estado atual pra que End()/SessionChange possa enviar a
                // ultima volta caso o trigger normal nao consiga (saida abrupta).
                // ============================================================
                if (FIX_FINAL_LAP_FLUSH && newData.CompletedLaps > 0)
                {
                    UpdateFinalLapSnapshot(newData, data, compound, playerOpp);
                }

                _lastCompletedLaps = newData.CompletedLaps;
                _totalLaps = newData.TotalLaps;
            }
            catch (Exception ex)
            {
                // v2.5.2 — stack trace completa pra diagnostico
                Log("DataUpdate error: " + ex.GetType().Name + ": " + ex.Message);
                Log("DataUpdate stack: " + (ex.StackTrace ?? "(no stack)"));
                if (ex.InnerException != null)
                {
                    Log("DataUpdate inner: " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message);
                }
            }
        }

        // ============================================================
        // v2.5.0 FIX 1 — atualiza a snapshot da ultima volta vista
        // ============================================================
        private void UpdateFinalLapSnapshot(StatusDataBase d, GameData data, string compound, Opponent playerOpp)
        {
            try
            {
                double lapTimeSec = d.LastLapTime.TotalSeconds;
                // Snapshot so vale a pena se temos LastLapTime valido — senao nao tem o que flushear
                if (lapTimeSec < 5.0) return;

                int oppLap = 0;
                double oppLastLap = 0.0;
                if (playerOpp != null)
                {
                    oppLap = SafeInt(GetMember(playerOpp, "CurrentLap"));
                    oppLastLap = ParseLapTimeSeconds(GetMember(playerOpp, "LastLapTime"));
                }

                _lastSnapshot = new FinalLapSnapshot
                {
                    CompletedLaps = d.CompletedLaps,
                    LastLapTimeSec = lapTimeSec,
                    S1Sec = d.Sector1LastLapTime?.TotalSeconds ?? 0.0,
                    S2Sec = d.Sector2LastLapTime?.TotalSeconds ?? 0.0,
                    S3Sec = d.Sector3LastLapTime?.TotalSeconds ?? 0.0,
                    SpeedKmh = d.SpeedKmh,
                    TyreWearFL = SafeDouble(d.TyreWearFrontLeft),
                    TyreWearFR = SafeDouble(d.TyreWearFrontRight),
                    TyreWearRL = SafeDouble(d.TyreWearRearLeft),
                    TyreWearRR = SafeDouble(d.TyreWearRearRight),
                    Fuel = SafeDouble(d.Fuel),
                    Position = d.Position,
                    Compound = compound ?? "unknown",
                    RoadTemp = SafeDouble(d.RoadTemperature),
                    AirTemp = SafeDouble(d.AirTemperature),
                    TraceCopy = new List<Dictionary<string, object>>(_traceBuffer),
                    OppLap = oppLap,
                    OppLastLap = oppLastLap,
                    InputThrottleSamples = _playerThrottleSamples,
                    InputSpeedSamples = _playerSpeedSamples,
                    TotalSamples = _totalSamplesThisLap,
                    IsOnline = DetectOnlineSession(data),
                    HasPlayerOpponent = playerOpp != null
                };
                // se uma volta nova foi capturada na snapshot, marca o flush como "nao realizado"
                if (d.CompletedLaps > _lastSentLapNumber)
                {
                    _finalLapFlushed = false;
                }
            }
            catch (Exception ex)
            {
                Log("UpdateFinalLapSnapshot error: " + ex.Message);
            }
        }

        // ============================================================
        // v2.5.0 FIX 2 — detectar se a sessao eh online (via reflection no GameRawData)
        // Em F1 25 UDP, o campo m_networkGame indica se eh sessao online.
        // Se nao conseguirmos detectar (campo nao exposto), retornamos false (offline).
        // ============================================================
        // ============================================================
        // v2.5.2 — detectar se jogo esta pausado (via reflection no SDB)
        // ============================================================
        private bool SafeGetGamePaused(GameData data)
        {
            try
            {
                if (data == null) return false;
                // Tenta direto em GameData (mais comum)
                object v = GetMember(data, "GamePaused") ?? GetMember(data, "IsPaused");
                if (v is bool b1) return b1;
                // Fallback: NewData
                if (data.NewData != null)
                {
                    object v2 = GetMember(data.NewData, "GamePaused") ?? GetMember(data.NewData, "IsPaused");
                    if (v2 is bool b2) return b2;
                }
            }
            catch { }
            return false;
        }

        private bool DetectOnlineSession(GameData data)
        {
            try
            {
                object raw = GetMember(data?.NewData, "GameRawData");
                if (raw == null && data != null) raw = GetMember(data, "GameRawData");
                if (raw == null) return false;
                double? v = FindNumeric(raw, "networkGame", "m_networkGame", "NetworkGame", "isOnline", "IsOnline");
                if (v.HasValue) return v.Value > 0.5;
            }
            catch { }
            return false;
        }

        private void ResetLapInputCounters()
        {
            _totalSamplesThisLap = 0;
            _playerThrottleSamples = 0;
            _playerBrakeSamples = 0;
            _playerSpeedSamples = 0;
        }

        // === v2.4.1: find the Opponent flagged IsPlayer ===
        private static Opponent FindPlayerOpponent(StatusDataBase newData)
        {
            var opps = newData?.Opponents;
            if (opps == null) return null;
            foreach (var o in opps)
            {
                try
                {
                    object m = GetMember(o, "IsPlayer");
                    if (m is bool b && b) return o;
                }
                catch { }
            }
            return null;
        }

        private static double ParseLapTimeSeconds(object v)
        {
            if (v == null) return 0.0;
            if (v is TimeSpan ts) return ts.TotalSeconds;
            if (v is double d) return d;
            if (v is float f) return f;
            try
            {
                string s = v.ToString();
                if (string.IsNullOrEmpty(s)) return 0.0;
                if (TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var t)) return t.TotalSeconds;
                if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var dv)) return dv;
            }
            catch { }
            return 0.0;
        }

        private static int SafeInt(object v)
        {
            if (v == null) return 0;
            try { return Convert.ToInt32(v); } catch { return 0; }
        }

        private void TryAddRawFields(GameData data, Dictionary<string, object> sample)
        {
            try
            {
                object obj = GetMember(data.NewData, "GameRawData") ?? GetMember(data, "GameRawData");
                if (obj != null)
                {
                    AddNumeric(sample, "ld", FindNumeric(obj, "lapDistance", "LapDistance", "m_lapDistance"));
                    AddNumeric(sample, "tp", FindNumeric(obj, "totalDistance", "TotalDistance", "m_totalDistance"));
                    AddNumeric(sample, "wx", FindNumeric(obj, "worldPositionX", "WorldPositionX", "m_worldPositionX"));
                    AddNumeric(sample, "wy", FindNumeric(obj, "worldPositionZ", "WorldPositionZ", "m_worldPositionZ", "worldPositionY", "WorldPositionY"));
                }
            }
            catch { }
        }

        private static void AddNumeric(Dictionary<string, object> dict, string key, double? value)
        {
            if (value.HasValue) dict[key] = Math.Round(value.Value, 3);
        }

        private static double? FindNumeric(object root, params string[] names)
        {
            if (root == null) return null;
            return SearchNumeric(root, names, 0, new HashSet<object>());
        }

        private static double? SearchNumeric(object obj, string[] names, int depth, HashSet<object> visited)
        {
            if (obj == null || depth > 4) return null;
            if (!obj.GetType().IsValueType)
            {
                if (visited.Contains(obj)) return null;
                visited.Add(obj);
            }
            Type type = obj.GetType();
            foreach (string name in names)
            {
                FieldInfo field = type.GetField(name, BindingFlags.IgnoreCase | BindingFlags.Instance | BindingFlags.Public);
                if (field != null) { var v = field.GetValue(obj); if (v != null) return ToDouble(v); }
                PropertyInfo prop = type.GetProperty(name, BindingFlags.IgnoreCase | BindingFlags.Instance | BindingFlags.Public);
                if (prop != null && prop.CanRead)
                {
                    try { var v = prop.GetValue(obj, null); if (v != null) return ToDouble(v); } catch { }
                }
            }
            foreach (PropertyInfo p in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                Type pt = p.PropertyType;
                if (pt.IsPrimitive || pt == typeof(string) || pt == typeof(decimal)) continue;
                try
                {
                    object child = p.GetValue(obj, null);
                    if (child != null)
                    {
                        var found = SearchNumeric(child, names, depth + 1, visited);
                        if (found.HasValue) return found;
                    }
                }
                catch { }
            }
            return null;
        }

        private static double? ToDouble(object v)
        {
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return null; }
        }

        private static object GetMember(object obj, string name)
        {
            if (obj == null) return null;
            Type type = obj.GetType();
            PropertyInfo prop = type.GetProperty(name, BindingFlags.IgnoreCase | BindingFlags.Instance | BindingFlags.Public);
            if (prop != null && prop.CanRead)
            {
                try { return prop.GetValue(obj, null); } catch { return null; }
            }
            FieldInfo field = type.GetField(name, BindingFlags.IgnoreCase | BindingFlags.Instance | BindingFlags.Public);
            if (field != null) return field.GetValue(obj);
            return null;
        }

        private string ExtractCompound(GameData data)
        {
            try
            {
                var newData = data.NewData;
                var list = newData?.Opponents;
                if (list != null)
                {
                    foreach (var item in list)
                    {
                        try
                        {
                            object pm = GetMember(item, "IsPlayer");
                            if (pm is bool b && b)
                            {
                                object obj2 = GetMember(item, "FrontTyreCompound") ?? GetMember(item, "TyreCompound");
                                if (obj2 != null) return obj2.ToString();
                            }
                        }
                        catch { }
                    }
                }
                object m2 = GetMember(data.NewData, "m_visualTyreCompound");
                if (m2 != null) return m2.ToString();
            }
            catch { }
            return "unknown";
        }

        private static double SafeDouble(object v)
        {
            if (v == null) return 0.0;
            try { return Convert.ToDouble(v); } catch { return 0.0; }
        }

        private bool SendLap(StatusDataBase d, GameData data, string compound, Opponent playerOpp, int lapNumberOverride = -1)
        {
            try
            {
                // v2.5.3: se for fallback, usa o override; senao, usa o CompletedLaps do SDB
                int completedLaps = lapNumberOverride > 0 ? lapNumberOverride : d.CompletedLaps;
                double lapTimeSec = d.LastLapTime.TotalSeconds;
                double s1 = d.Sector1LastLapTime?.TotalSeconds ?? 0.0;
                double s2 = d.Sector2LastLapTime?.TotalSeconds ?? 0.0;
                double s3 = d.Sector3LastLapTime?.TotalSeconds ?? 0.0;

                // === v2.5.0 ANTI-SPECTATOR VALIDATION (endurecida) ===
                bool isValid = true;
                List<string> rejectReasons = new List<string>();

                // === v2.5.0 FIX 2a — detectar se eh sessao online ===
                bool isOnline = DetectOnlineSession(data);

                // === v2.5.0 FIX 2b — em online, exigir playerOpp ===
                // Em sessoes online (lobbies multiplayer), o IsPlayer=true do Opponent eh
                // a unica fonte confiavel pra distinguir nosso carro de outros. Se nao
                // achamos esse Opponent, eh fortissima suspeita de modo broadcast/spectator.
                // Em offline (single player) nao exigimos pq alguns modos nao expoem isso.
                if (FIX_STRICT_OPPONENT && isOnline && playerOpp == null)
                {
                    isValid = false;
                    rejectReasons.Add("online_without_player_opponent");
                }

                // (1) Cross-check with player's own opponent record
                if (playerOpp != null && lapNumberOverride <= 0)
                {
                    // v2.5.3: cross-check de playerOpp NAO se aplica em fallback
                    // (no cenario fallback, F1 25 trava CompletedLaps E playerOpp.CurrentLap juntos,
                    //  entao a comparacao "oppLap-1-completedLaps" daria sempre mismatch.
                    //  A defesa anti-spectator no fallback fica pelos checks de input ratio abaixo.)
                    int oppLap = SafeInt(GetMember(playerOpp, "CurrentLap"));
                    double oppLastLap = ParseLapTimeSeconds(GetMember(playerOpp, "LastLapTime"));

                    // Player opponent lap counter should match (or be within 1 of) StatusDataBase
                    if (oppLap > 0 && Math.Abs(oppLap - 1 - completedLaps) > 1)
                    {
                        isValid = false;
                        rejectReasons.Add("opponent_lap_mismatch(opp=" + oppLap + ",sdb=" + completedLaps + ")");
                    }

                    // === v2.5.0 FIX 2c — sem Frankenstein ===
                    // Se o lap_time do playerOpp diverge do SDB, REJEITAMOS A VOLTA INTEIRA.
                    // A v2.4.x "consertava" o lap_time mas mantinha top_speed/wear/sectors do SDB
                    // (que podia estar com dados do carro espectado). Resultado: volta misturada.
                    // Agora: ou aceita tudo do SDB (validado) ou rejeita tudo. Sem meio-termo.
                    if (oppLastLap > 5.0 && lapTimeSec > 5.0 && Math.Abs(oppLastLap - lapTimeSec) > 0.5)
                    {
                        isValid = false;
                        rejectReasons.Add("lap_time_mismatch(opp=" + oppLastLap.ToString("F3") + ",sdb=" + lapTimeSec.ToString("F3") + ")");
                    }

                    // === v2.5.0 — REMOVIDO o "lapTimeSec = oppLastLap" (Frankenstein) ===
                    // Se a divergencia eh pequena (< 0.5s), aceita tudo do SDB sem alterar.
                    // Se for grande, ja foi rejeitada acima.
                }

                // (2) Input-activity heuristic: a real player driving should produce
                //     throttle and speed samples on most of the lap.
                if (_totalSamplesThisLap >= 50) // ~5s of samples minimum
                {
                    double thrRatio = (double)_playerThrottleSamples / _totalSamplesThisLap;
                    double spdRatio = (double)_playerSpeedSamples / _totalSamplesThisLap;
                    if (spdRatio < 0.3)
                    {
                        isValid = false;
                        rejectReasons.Add("low_speed_activity(" + spdRatio.ToString("F2") + ")");
                    }
                    if (thrRatio < 0.05)
                    {
                        isValid = false;
                        rejectReasons.Add("no_throttle_activity(" + thrRatio.ToString("F2") + ")");
                    }
                }

                // (3) Sanity: lap_time must be plausible (5s..600s)
                if (lapTimeSec < 5.0 || lapTimeSec > 600.0)
                {
                    isValid = false;
                    rejectReasons.Add("lap_time_out_of_range(" + lapTimeSec.ToString("F3") + ")");
                }

                if (!isValid)
                {
                    Log("LAP REJECTED #" + completedLaps + " reasons=[" + string.Join(",", rejectReasons.ToArray()) + "]");
                    _traceBuffer.Clear();
                    return false;
                }

                var lap = new Dictionary<string, object> {
                    { "lap_number", completedLaps },
                    { "lap_time", lapTimeSec },
                    { "sector1_time", s1 > 0 ? (object)s1 : null },
                    { "sector2_time", s2 > 0 ? (object)s2 : null },
                    { "sector3_time", s3 > 0 ? (object)s3 : null },
                    { "top_speed", d.SpeedKmh },
                    { "tire_compound", compound },
                    { "tire_age_laps", _tireAgeLaps },
                    { "tire_wear_fl", SafeDouble(d.TyreWearFrontLeft) },
                    { "tire_wear_fr", SafeDouble(d.TyreWearFrontRight) },
                    { "tire_wear_rl", SafeDouble(d.TyreWearRearLeft) },
                    { "tire_wear_rr", SafeDouble(d.TyreWearRearRight) },
                    { "fuel_remaining", SafeDouble(d.Fuel) },
                    { "ers_stored", 0.0 },
                    { "position", d.Position },
                    { "is_valid", true },
                    { "stint_number", _stintNumber },
                    { "stint_compound", _currentCompound ?? compound },
                    { "stint_start_lap", _stintStartLap },
                    { "stint_start_wear", _stintStartWear },
                    { "trace_data", _traceBuffer.ToList() },
                    // v2.4.1 telemetry of validation
                    { "input_throttle_ratio", _totalSamplesThisLap > 0 ? (double)_playerThrottleSamples / _totalSamplesThisLap : 0.0 },
                    { "input_speed_ratio",    _totalSamplesThisLap > 0 ? (double)_playerSpeedSamples    / _totalSamplesThisLap : 0.0 },
                    { "samples_in_lap", _totalSamplesThisLap }
                };
                _traceBuffer.Clear();

                var sessionInfo = new Dictionary<string, object> {
                    { "track_name", _currentTrack ?? "" },
                    { "session_type", _sessionType ?? "race" },
                    { "weather", "" },
                    { "track_temperature", SafeDouble(d.RoadTemperature) },
                    { "air_temperature", SafeDouble(d.AirTemperature) },
                    { "session_uid", _sessionUid }
                };

                var msg = new Dictionary<string, object> {
                    { "pilot_token", _apiToken },
                    { "action", "lap" },
                    { "lap", lap },
                    { "session_info", sessionInfo },
                    { "completed_laps", completedLaps },
                    { "total_laps", _totalLaps },
                    { "plugin_version", VERSION }
                };
                if (!string.IsNullOrEmpty(_sessionId)) msg["session_id"] = _sessionId;

                string resp = SendData(msg);
                if (!string.IsNullOrEmpty(resp) && string.IsNullOrEmpty(_sessionId))
                {
                    int idx = resp.IndexOf("\"session_id\"");
                    if (idx >= 0)
                    {
                        int q1 = resp.IndexOf("\"", idx + 12);
                        int q2 = resp.IndexOf("\"", q1 + 1);
                        if (q1 > 0 && q2 > q1) _sessionId = resp.Substring(q1 + 1, q2 - q1 - 1);
                    }
                }
                Log("Lap " + completedLaps + " sent. Compound: " + compound + " Stint: " + _stintNumber + " Time: " + lapTimeSec.ToString("F3"));
                return true;
            }
            catch (Exception ex) { Log("Send lap error: " + ex.Message); return false; }
        }

        private string SendData(Dictionary<string, object> data)
        {
            string s = ToJson(data);
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(_endpoint);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.UserAgent = "SimSetups-TelemetryPlugin/" + VERSION;
            req.ContentLength = bytes.Length;
            req.Timeout = 10000;
            using (Stream st = req.GetRequestStream()) st.Write(bytes, 0, bytes.Length);
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream()))
                return sr.ReadToEnd();
        }

        private static string ToJson(object obj)
        {
            var sb = new StringBuilder();
            WriteJson(sb, obj);
            return sb.ToString();
        }

        private static void WriteJson(StringBuilder sb, object obj)
        {
            if (obj == null) { sb.Append("null"); return; }
            if (obj is string s) { sb.Append('"'); EscapeJson(sb, s); sb.Append('"'); return; }
            if (obj is bool b) { sb.Append(b ? "true" : "false"); return; }
            if (obj is IDictionary<string, object> dict)
            {
                sb.Append('{'); bool first = true;
                foreach (var kv in dict)
                {
                    if (!first) sb.Append(','); first = false;
                    sb.Append('"'); EscapeJson(sb, kv.Key); sb.Append("\":");
                    WriteJson(sb, kv.Value);
                }
                sb.Append('}'); return;
            }
            if (obj is IEnumerable en && !(obj is string))
            {
                sb.Append('['); bool first = true;
                foreach (var it in en)
                {
                    if (!first) sb.Append(','); first = false;
                    WriteJson(sb, it);
                }
                sb.Append(']'); return;
            }
            if (obj is double dv) { sb.Append(dv.ToString(CultureInfo.InvariantCulture)); return; }
            if (obj is float fv) { sb.Append(fv.ToString(CultureInfo.InvariantCulture)); return; }
            if (obj is decimal mv) { sb.Append(mv.ToString(CultureInfo.InvariantCulture)); return; }
            if (obj is IFormattable f) { sb.Append(f.ToString(null, CultureInfo.InvariantCulture)); return; }
            sb.Append('"'); EscapeJson(sb, obj.ToString()); sb.Append('"');
        }

        private static void EscapeJson(StringBuilder sb, string s)
        {
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\b': sb.Append("\\b"); continue;
                    case '\f': sb.Append("\\f"); continue;
                    case '\n': sb.Append("\\n"); continue;
                    case '\r': sb.Append("\\r"); continue;
                    case '\t': sb.Append("\\t"); continue;
                }
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c < ' ') sb.AppendFormat("\\u{0:x4}", (int)c);
                else sb.Append(c);
            }
        }

        // ============================================================
        // v2.5.1 — Logging em arquivo proprio
        // Console.WriteLine nao aparece no log do SimHub (descartado pelo NLog interno).
        // Escrevemos em arquivo "SimSetups_Plugin.log" na mesma pasta do Token.txt.
        // Usuario pode abrir esse arquivo pra debug ou nos enviar quando algo der ruim.
        // ============================================================
        private void Log(string msg)
        {
            string ts = DateTime.Now.ToString("HH:mm:ss.fff");
            string line = "[" + ts + "] " + msg;
            try { Console.WriteLine("[SimSetups] " + line); } catch { }
            if (string.IsNullOrEmpty(_logFilePath)) return;
            try
            {
                File.AppendAllText(_logFilePath, line + Environment.NewLine);
            }
            catch { }
        }
    }
}
