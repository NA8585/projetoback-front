// IRacingTelemetryService.cs
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using IRSDKSharper; // Biblioteca IRSDKSharper para conexão com iRacing
using SuperBackendNR85IA.Models; // TelemetryModel e classes auxiliares
using SuperBackendNR85IA.Calculations; // Seus cálculos de telemetria personalizados

namespace SuperBackendNR85IA.Services
{
    public sealed class IRacingTelemetryService : BackgroundService
    {
        private const int TICK_MS = 16; // ~60 Hz
        private readonly ILogger<IRacingTelemetryService> _log;
        private readonly TelemetryBroadcaster _broadcaster;
        private readonly IRacingSdk _sdk = new();
        private readonly SessionYamlParser _yamlParser = new();

        private string _lastYaml = string.Empty;
        private (DriverInfo? Drv, WeekendInfo? Wkd, SessionInfo? Ses,SectorInfo Sec) _cachedYamlData;
        private int _lastTick = -1;

        public IRacingTelemetryService(ILogger<IRacingTelemetryService> log, TelemetryBroadcaster broadcaster)
        {
            _log = log;
            _broadcaster = broadcaster;
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            _log.LogInformation("IRacingTelemetryService está iniciando.");

            _sdk.OnConnected += () => _log.LogInformation("SDK Conectado ao iRacing.");
            _sdk.OnDisconnected += () => _log.LogInformation("SDK Desconectado do iRacing.");
            _sdk.OnException += (ex) => _log.LogError(ex, "Exceção no IRSDKSharper.");

            try
            {
                _sdk.Start(); // Inicia o monitoramento do iRacing
                _log.LogInformation("IRSDKSharper iniciado e aguardando conexão com o iRacing.");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Falha ao iniciar IRSDKSharper.");
                return;
            }

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!_sdk.IsConnected || !_sdk.IsStarted)
                    {
                        await Task.Delay(1000, ct);
                        continue;
                    }

                    if (_sdk.Data != null && _sdk.Data.TickCount != _lastTick)
                    {
                        var telemetryModel = BuildTelemetryModel();
                        if (telemetryModel != null)
                        {
                            await _broadcaster.BroadcastTelemetry(telemetryModel);
                        }
                        _lastTick = _sdk.Data.TickCount;
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Erro no loop principal de telemetria.");
                }
                await Task.Delay(TICK_MS, ct);
            }

            _log.LogInformation("IRacingTelemetryService está parando.");
            _sdk.Stop();
        }

        private T? GetSdkValue<T>(IRacingSdkData data, string varName) where T : struct
        {
            try
            {
                if (!data.TelemetryDataProperties.TryGetValue(varName, out var datum)) return null;
                if (datum.Count == 0) return null;

                object? value = null;
                if (typeof(T) == typeof(float)) value = data.GetFloat(datum);
                else if (typeof(T) == typeof(int)) value = data.GetInt(datum);
                else if (typeof(T) == typeof(bool)) value = data.GetBool(datum);
                else if (typeof(T) == typeof(double)) value = data.GetDouble(datum);
                else
                {
                    _log.LogWarning($"Tipo não suportado em GetSdkValue: {typeof(T)} para variável {varName}");
                    return null;
                }
                return (T?)value;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, $"Erro ao acessar variável {varName} como {typeof(T)}");
                return null;
            }
        }

        private string? GetSdkString(IRacingSdkData data, string varName)
        {
            try
            {
                if (!data.TelemetryDataProperties.TryGetValue(varName, out var datum)) return null;
                if (datum.Count == 0) return null;
                var value = data.GetValue(datum);
                if (value is char[] charArray) return new string(charArray).TrimEnd('\0');
                return value?.ToString()?.TrimEnd('\0');
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, $"Erro ao acessar string {varName}");
                return null;
            }
        }

        private T?[] GetSdkArray<T>(IRacingSdkData data, string varName) where T : struct
        {
            try
            {
                if (!data.TelemetryDataProperties.TryGetValue(varName, out var datum) || datum.Count == 0)
                {
                    return Array.Empty<T?>();
                }

                var arr = new T?[datum.Count];
                if (typeof(T) == typeof(float))
                {
                    float[] floatArr = new float[datum.Count];
                    data.GetFloatArray(datum, floatArr, 0, datum.Count);
                    for (int i = 0; i < datum.Count; i++) arr[i] = (T?)(object)floatArr[i];
                }
                else if (typeof(T) == typeof(int))
                {
                    int[] intArr = new int[datum.Count];
                    data.GetIntArray(datum, intArr, 0, datum.Count);
                    for (int i = 0; i < datum.Count; i++) arr[i] = (T?)(object)intArr[i];
                }
                else if (typeof(T) == typeof(bool))
                {
                    bool[] boolArr = new bool[datum.Count];
                    data.GetBoolArray(datum, boolArr, 0, datum.Count);
                    for (int i = 0; i < datum.Count; i++) arr[i] = (T?)(object)boolArr[i];
                }
                else if (typeof(T) == typeof(double))
                {
                    double[] doubleArr = new double[datum.Count];
                    data.GetDoubleArray(datum, doubleArr, 0, datum.Count);
                    for (int i = 0; i < datum.Count; i++) arr[i] = (T?)(object)doubleArr[i];
                }
                else
                {
                    _log.LogWarning($"Tipo de array não suportado em GetSdkArray: {typeof(T)} para variável {varName}");
                    return Array.Empty<T?>();
                }
                return arr;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, $"Erro ao acessar array {varName} como {typeof(T)}[]");
                return Array.Empty<T?>();
            }
        }

        private TelemetryModel? BuildTelemetryModel()
        {
            if (_sdk.Data == null) return null;

            var d = _sdk.Data;
            var t = new TelemetryModel();

            // --- Dados básicos do veículo ---
            t.Speed               = GetSdkValue<float>(d, "Speed") ?? 0f;
            t.Rpm                 = GetSdkValue<float>(d, "RPM") ?? 0f;
            t.Throttle            = GetSdkValue<float>(d, "Throttle") ?? 0f;
            t.Brake               = GetSdkValue<float>(d, "Brake") ?? 0f;
            t.Clutch              = GetSdkValue<float>(d, "Clutch") ?? 0f;
            t.SteeringWheelAngle  = GetSdkValue<float>(d, "SteeringWheelAngle") ?? 0f;
            t.Gear                = GetSdkValue<int>(d, "Gear") ?? 0;
            t.FuelLevel           = GetSdkValue<float>(d, "FuelLevel") ?? 0f;
            t.FuelLevelPct        = GetSdkValue<float>(d, "FuelLevelPct") ?? 0f;
            t.WaterTemp           = GetSdkValue<float>(d, "WaterTemp") ?? 0f;
            t.OilTemp             = GetSdkValue<float>(d, "OilTemp") ?? 0f;
            t.OilPress            = GetSdkValue<float>(d, "OilPress") ?? 0f;
            t.FuelPress           = GetSdkValue<float>(d, "FuelPress") ?? 0f;
            t.ManifoldPress       = GetSdkValue<float>(d, "ManifoldPress") ?? 0f;
            t.EngineWarnings      = GetSdkValue<int>(d, "EngineWarnings") ?? 0;
            t.OnPitRoad           = GetSdkValue<bool>(d, "OnPitRoad") ?? false;
            t.PlayerCarLastPitTime = GetSdkValue<float>(d, "PlayerCarLastPitTime") ?? 0f;
            t.PlayerCarPitStopCount = GetSdkValue<int>(d, "PlayerCarPitStopCount") ?? 0;
            t.PitRepairLeft       = GetSdkValue<float>(d, "PitRepairLeft") ?? 0f;
            t.PitOptRepairLeft    = GetSdkValue<float>(d, "PitOptRepairLeft") ?? 0f;

            // CarSpeed para FFB parado
            t.CarSpeed = t.Speed;

            // --- Dados de volta ---
            t.Lap                         = GetSdkValue<int>(d, "Lap") ?? 0;
            t.LapDistPct                  = GetSdkValue<float>(d, "LapDistPct") ?? 0f;
            t.LapCurrentLapTime           = GetSdkValue<float>(d, "LapCurrentLapTime") ?? 0f;
            t.LapLastLapTime              = GetSdkValue<float>(d, "LapLastLapTime") ?? 0f;
            t.LapBestLapTime              = GetSdkValue<float>(d, "LapBestLapTime") ?? 0f;
            t.LapDeltaToSessionBestLap    = GetSdkValue<float>(d, "LapDeltaToSessionBestLap") ?? 0f;
            t.LapDeltaToSessionOptimalLap = GetSdkValue<float>(d, "LapDeltaToSessionOptimalLap") ?? 0f;
            t.LapDeltaToDriverBestLap     = GetSdkValue<float>(d, "LapDeltaToPlayerBestLap") ?? 0f;

            // ─────────────────────────────────────────────────────────────────────────
            // Coleta de SETORES (arrays prontas do SDK)
            // ─────────────────────────────────────────────────────────────────────────

            float[] Arr(params string[] names)
            {
                foreach (var nomeVar in names)
                {
                    var raw = GetSdkArray<float>(d, nomeVar);
                    if (raw != null && raw.Length > 0 && raw.Any(v => v.HasValue))
                        return raw.Select(v => v ?? 0f).ToArray();
                }
                return Array.Empty<float>();
            }

            // Última volta (setores) — pré-2023: "LapLastLapSectorTimes" | 2023+: "SectorTimeSessionLastLap"
            t.LapAllSectorTimes = Arr("LapLastLapSectorTimes", "SectorTimeSessionLastLap");

            // Melhores setores da sessão — pré-2023: "SessionBestSectorTimes" | 2023+: "SectorTimeSessionFastestLap"
            t.SessionBestSectorTimes = Arr("SessionBestSectorTimes", "SectorTimeSessionFastestLap");

            // Delta piloto × melhores setores da sessão — pré-2023: "LapDeltaToSessionBestSectorTimes" | 2023+: "SectorTimeDeltaSessionBestLap"
            if (t.LapAllSectorTimes.Length > 0 && t.SessionBestSectorTimes.Length == t.LapAllSectorTimes.Length)
            {
                int count = t.LapAllSectorTimes.Length;
                var deltas = new float[count];
                for (int i = 0; i < count; i++)
                {
                    var tp = t.LapAllSectorTimes[i];
                    var ts = t.SessionBestSectorTimes[i];
                    if (tp > 1e-4f && ts > 1e-4f)
                        deltas[i] = tp - ts;
                    else
                        deltas[i] = 0f;
                }
                t.LapDeltaToSessionBestSectorTimes = deltas;
            }
            else
            {
                t.LapDeltaToSessionBestSectorTimes = Arr("LapDeltaToSessionBestSectorTimes", "SectorTimeDeltaSessionBestLap");
            }

            // Optimal Lap Time — tenta "LapOptimalLapTime" se existir, senão soma dos melhores setores
            var lapOpt = GetSdkValue<float>(d, "LapOptimalLapTime") ?? 0f;
            t.EstLapTime = lapOpt > 1e-4f ? lapOpt : t.LapBestLapTime;

            // ─────────────────────────────────────────────────────────────────────────
            // Force Feedback (FFB)
            // ─────────────────────────────────────────────────────────────────────────

            t.FfbPercent = GetSdkValue<float>(d, "ForceFeedbackPct") ?? 0f;
            t.FfbClip    = GetSdkValue<bool>(d, "ForceFeedbackClip") ?? false;

            if (t.FfbPercent <= 0f)
            {
                // Se não existir ou for zero, calcular pelo torque
                var torqueNm = GetSdkValue<float>(d, "SteeringWheelTorque") ?? 0f;
                var maxForceNm = GetSdkValue<float>(d, "SteeringWheelMaxForce") ?? 6f;
                if (maxForceNm <= 0f) maxForceNm = 6f;
                t.FfbPercent = MathF.Min(MathF.Abs(torqueNm) / maxForceNm, 1f);
                t.FfbClip = t.FfbPercent >= 0.98f;
            }

            // ─────────────────────────────────────────────────────────────────────────
            // Distância Relativa (frente / trás)
            // ─────────────────────────────────────────────────────────────────────────

            var lapPctArr = GetSdkArray<float>(d, "CarIdxLapDistPct")?.Select(v => v ?? 0f).ToArray() ?? Array.Empty<float>();
            var posArr    = GetSdkArray<int>(d, "CarIdxPosition")?.Select(v => v ?? 0).ToArray() ?? Array.Empty<int>();
            int myIdx     = GetSdkValue<int>(d, "PlayerCarIdx") ?? -1;

            if (myIdx >= 0 && myIdx < lapPctArr.Length && lapPctArr.Length == posArr.Length)
            {
                float myPct = lapPctArr[myIdx];
                float trackKm = GetSdkValue<float>(d, "TrackLength") ?? 1f; // já em km
                float bestA = 1f, bestB = 1f;

                for (int i = 0; i < lapPctArr.Length; i++)
                {
                    if (i == myIdx) continue;
                    float otherPct = lapPctArr[i];
                    if (otherPct <= 0f) continue;

                    float delta = otherPct - myPct;
                    if (delta < -0.5f) delta += 1f;
                    if (delta >  0.5f) delta -= 1f;

                    if (delta > 0f && delta < bestA) bestA = delta;
                    if (delta < 0f && -delta < bestB) bestB = -delta;
                }

                t.DistanceAhead  = float.IsInfinity(bestA)  ? -1f : bestA  * trackKm * 1000f;
                t.DistanceBehind = float.IsInfinity(bestB) ? -1f : bestB * trackKm * 1000f;

                t.CarIdxLapDistPct = lapPctArr;
                t.CarIdxPosition   = posArr;
            }
            else
            {
                t.DistanceAhead    = -1f;
                t.DistanceBehind   = -1f;
                t.CarIdxLapDistPct = Array.Empty<float>();
                t.CarIdxPosition   = Array.Empty<int>();
            }

            // --- Sessão ---
            t.SessionNum        = GetSdkValue<int>(d, "SessionNum") ?? 0;
            t.SessionTime       = GetSdkValue<float>(d, "SessionTime") ?? 0f;
            t.SessionTimeRemain = GetSdkValue<float>(d, "SessionTimeRemain") ?? 0f;
            t.SessionState      = GetSdkValue<int>(d, "SessionState") ?? 0;
            t.PaceMode          = GetSdkValue<int>(d, "PaceMode") ?? 0;
            t.SessionFlags      = GetSdkValue<int>(d, "SessionFlags") ?? 0;
            t.PlayerCarIdx      = GetSdkValue<int>(d, "PlayerCarIdx") ?? -1;
            t.TotalLaps         = GetSdkValue<int>(d, "CurrentSessionTotalLaps") ?? -1;
            t.LapsRemainingRace = GetSdkValue<int>(d, "LapsRemainingRace")    ?? 0;

            // --- Pneus e Temperaturas ---
            t.LfTempCl = GetSdkValue<float>(d, "LFtempCL") ?? 0f;
            t.LfTempCm = GetSdkValue<float>(d, "LFtempCM") ?? 0f;
            t.LfTempCr = GetSdkValue<float>(d, "LFtempCR") ?? 0f;
            t.RfTempCl = GetSdkValue<float>(d, "RFtempCL") ?? 0f;
            t.RfTempCm = GetSdkValue<float>(d, "RFtempCM") ?? 0f;
            t.RfTempCr = GetSdkValue<float>(d, "RFtempCR") ?? 0f;
            t.LrTempCl = GetSdkValue<float>(d, "LRtempCL") ?? 0f;
            t.LrTempCm = GetSdkValue<float>(d, "LRtempCM") ?? 0f;
            t.LrTempCr = GetSdkValue<float>(d, "LRtempCR") ?? 0f;
            t.RrTempCl = GetSdkValue<float>(d, "RRtempCL") ?? 0f;
            t.RrTempCm = GetSdkValue<float>(d, "RRtempCM") ?? 0f;
            t.RrTempCr = GetSdkValue<float>(d, "RRtempCR") ?? 0f;

            t.LfPress = GetSdkValue<float>(d, "LFpressure") ?? 0f;
            t.RfPress = GetSdkValue<float>(d, "RFpressure") ?? 0f;
            t.LrPress = GetSdkValue<float>(d, "LRpressure") ?? 0f;
            t.RrPress = GetSdkValue<float>(d, "RRpressure") ?? 0f;

            t.LfWear = new float?[] {
                GetSdkValue<float>(d, "LFWearL"),
                GetSdkValue<float>(d, "LFWearM"),
                GetSdkValue<float>(d, "LFWearR")
            }.Select(v => v ?? 0f).ToArray();
            t.RfWear = new float?[] {
                GetSdkValue<float>(d, "RFWearL"),
                GetSdkValue<float>(d, "RFWearM"),
                GetSdkValue<float>(d, "RFWearR")
            }.Select(v => v ?? 0f).ToArray();
            t.LrWear = new float?[] {
                GetSdkValue<float>(d, "LRWearL"),
                GetSdkValue<float>(d, "LRWearM"),
                GetSdkValue<float>(d, "LRWearR")
            }.Select(v => v ?? 0f).ToArray();
            t.RrWear = new float?[] {
                GetSdkValue<float>(d, "RRWearL"),
                GetSdkValue<float>(d, "RRWearM"),
                GetSdkValue<float>(d, "RRWearR")
            }.Select(v => v ?? 0f).ToArray();

            t.TreadRemainingFl = GetSdkValue<float>(d, "LFWearM") ?? 0f;
            t.TreadRemainingFr = GetSdkValue<float>(d, "RFWearM") ?? 0f;
            t.TreadRemainingRl = GetSdkValue<float>(d, "LRWearM") ?? 0f;
            t.TreadRemainingRr = GetSdkValue<float>(d, "RRWearM") ?? 0f;

            t.PlayerCarTireCompound = GetSdkString(d, "PlayerCarTireCompound") ?? string.Empty;
            t.TireSetsUsed         = GetSdkValue<int>(d, "TireSetsUsed") ?? 0;
            t.TireSetsAvailable    = GetSdkValue<int>(d, "TireSetsAvailable") ?? 0;

            // --- Freios e controles ---
            t.BrakeTemp        = GetSdkArray<float>(d, "BrakeTemp").Select(v => v ?? 0f).ToArray();
            t.LfBrakeLinePress = GetSdkValue<float>(d, "LFbrakeLinePress") ?? 0f;
            t.RfBrakeLinePress = GetSdkValue<float>(d, "RFbrakeLinePress") ?? 0f;
            t.LrBrakeLinePress = GetSdkValue<float>(d, "LRbrakeLinePress") ?? 0f;
            t.RrBrakeLinePress = GetSdkValue<float>(d, "RRbrakeLinePress") ?? 0f;
            t.DcBrakeBias      = GetSdkValue<float>(d, "dcBrakeBias") ?? 0f;
            t.DcAbs            = GetSdkValue<int>(d, "dcABS") ?? 0;
            t.DcTractionControl= GetSdkValue<int>(d, "dcTractionControl") ?? 0;
            t.DcFrontWing      = GetSdkValue<int>(d, "dcFrontWing") ?? 0;
            t.DcRearWing       = GetSdkValue<int>(d, "dcRearWing") ?? 0;
            t.DcDiffEntry      = GetSdkValue<int>(d, "dcDiffEntry") ?? 0;
            t.DcDiffMiddle     = GetSdkValue<int>(d, "dcDiffMiddle") ?? 0;
            t.DcDiffExit       = GetSdkValue<int>(d, "dcDiffExit") ?? 0;

            // --- Dinâmica do veículo ---
            t.LfSuspPos    = GetSdkValue<float>(d, "LFsuspPos") ?? 0f;
            t.RfSuspPos    = GetSdkValue<float>(d, "RFsuspPos") ?? 0f;
            t.LrSuspPos    = GetSdkValue<float>(d, "LRsuspPos") ?? 0f;
            t.RrSuspPos    = GetSdkValue<float>(d, "RRsuspPos") ?? 0f;
            t.LfSuspVel    = GetSdkValue<float>(d, "LFsuspVel") ?? 0f;
            t.RfSuspVel    = GetSdkValue<float>(d, "RFsuspVel") ?? 0f;
            t.LrSuspVel    = GetSdkValue<float>(d, "LRsuspVel") ?? 0f;
            t.RrSuspVel    = GetSdkValue<float>(d, "RRsuspVel") ?? 0f;
            t.LfRideHeight = GetSdkValue<float>(d, "LFrideHeight") ?? 0f;
            t.RfRideHeight = GetSdkValue<float>(d, "RFrideHeight") ?? 0f;
            t.LrRideHeight = GetSdkValue<float>(d, "LRrideHeight") ?? 0f;
            t.RrRideHeight = GetSdkValue<float>(d, "RRrideHeight") ?? 0f;
            t.LatAccel     = GetSdkValue<float>(d, "LatAccel") ?? 0f;
            t.LonAccel     = GetSdkValue<float>(d, "LonAccel") ?? 0f;
            t.VertAccel    = GetSdkValue<float>(d, "VertAccel") ?? 0f;
            t.Yaw          = GetSdkValue<float>(d, "Yaw") ?? 0f;
            t.Pitch        = GetSdkValue<float>(d, "Pitch") ?? 0f;
            t.Roll         = GetSdkValue<float>(d, "Roll") ?? 0f;

            // --- Danos ---
            t.LfDamage        = GetSdkValue<float>(d, "LFdamage") ?? 0f;
            t.RfDamage        = GetSdkValue<float>(d, "RFdamage") ?? 0f;
            t.LrDamage        = GetSdkValue<float>(d, "LRdamage") ?? 0f;
            t.RrDamage        = GetSdkValue<float>(d, "RRdamage") ?? 0f;
            t.FrontWingDamage = GetSdkValue<float>(d, "FrontWingDamage") ?? 0f;
            t.RearWingDamage  = GetSdkValue<float>(d, "RearWingDamage") ?? 0f;
            t.EngineDamage    = GetSdkValue<float>(d, "EngineDamagePct") ?? 0f;
            t.GearboxDamage   = GetSdkValue<float>(d, "GearBoxDamagePct") ?? 0f;
            t.SuspensionDamage = (
                (GetSdkValue<float>(d, "LFsuspDamPct") ?? 0f) +
                (GetSdkValue<float>(d, "RFsuspDamPct") ?? 0f) +
                (GetSdkValue<float>(d, "LRsuspDamPct") ?? 0f) +
                (GetSdkValue<float>(d, "RRsuspDamPct") ?? 0f)
            ) / 4f;
            t.ChassisDamage = t.SuspensionDamage;

            // --- Sistemas especiais ---
            t.DrsStatus      = GetSdkValue<int>(d, "DrsStatus") ?? 0;
            t.CarIdxP2PCount = GetSdkArray<int>(d, "CarIdxP2P_Count").Select(v => v ?? 0).ToArray();
            t.CarIdxP2PStatus= GetSdkArray<int>(d, "CarIdxP2P_Status").Select(v => v ?? 0).ToArray();
            t.DcEnginePower  = GetSdkValue<int>(d, "dcMGUKDeploymentMode") ?? 0;

            // --- Condições da pista ---
            t.TrackSurfaceTemp = GetSdkValue<float>(d, "TrackTemp") ?? 0f;
            t.TrackTempCrew    = GetSdkValue<float>(d, "TrackTempCrew") ?? 0f;
            t.AirTemp          = GetSdkValue<float>(d, "AirTemp") ?? 0f;
            t.TempUnits        = (GetSdkValue<bool>(d, "DisplayUnits") ?? false) ? 1 : 0;
            t.SessionTimeOfDay = GetSdkValue<float>(d, "SessionTimeOfDay") ?? 0f;
            t.TrackSurfaceMaterial = 0;
            // Como não existe GetTrackGripStatus, deixamos em branco:
            t.TrackGripStatus  = string.Empty;
            t.TrackStatus      = string.Join(", ", EnumTranslations.TranslateSessionFlags(t.SessionFlags));

            // --- Combustível ---
            t.FuelUsePerHour = GetSdkValue<float>(d, "FuelUsePerHour") ?? 0f;
            t.FuelUsePerLap  = GetSdkValue<float>(d, "FuelUsePerLap") ?? 0f;
            t.FuelPerLap     = t.FuelUsePerLap;

            // --- YAML Raw ---
            t.SessionInfoYaml = _sdk.Data?.SessionInfoYaml ?? string.Empty;

            // --- Parse YAML (com cache) ---
            if (!string.IsNullOrEmpty(t.SessionInfoYaml) && t.SessionInfoYaml != _lastYaml)
            {
                _log.LogDebug($"Atualizando cache do YAML. PlayerCarIdx: {t.PlayerCarIdx}, SessionNum: {t.SessionNum}");
                _cachedYamlData = _yamlParser.ParseSessionInfo(
                    t.SessionInfoYaml,
                    t.PlayerCarIdx,
                    t.SessionNum
                );
                _lastYaml = t.SessionInfoYaml;
            }

            var (drv, wkd, ses, sec) = _cachedYamlData;

            if (drv != null)
            {
                t.UserName           = drv.UserName;
                t.TeamName           = drv.TeamName;
                t.CarNumber          = drv.CarNumber;
                t.IRating            = drv.IRating;
                t.LicString          = drv.LicString;
                t.LicSafetyRating    = drv.LicLevel + drv.LicSubLevel / 1000f;
                t.PlayerCarClassID   = drv.CarClassID;
            }
            t.PlayerCarTeamIncidentCount = GetSdkValue<int>(d, "PlayerCarTeamIncidentCount") ?? 0;

            if (wkd != null)
            {
                t.TrackDisplayName    = wkd.TrackDisplayName;
                t.TrackConfigName     = wkd.TrackConfigName;
                t.TrackLength         = wkd.TrackLengthKm;
                t.SessionTypeFromYaml = wkd.SessionType;
                t.Skies               = EnumTranslations.TranslateSkies(GetSdkValue<int>(d, "Skies") ?? 0);
                t.ForecastType        = wkd.ForecastType;
                t.WindSpeed           = wkd.WindSpeed;
                t.AirPressure         = wkd.AirPressure;
                t.RelativeHumidity    = wkd.RelativeHumidity;
                t.ChanceOfRain        = wkd.ChanceOfRain;
            }

            if (ses != null)
            {
                t.IncidentLimit = ses.IncidentLimit;
                t.TotalLaps = ses.CurrentSessionTotalLaps > 0
                    ? ses.CurrentSessionTotalLaps
                    : ((ses.SessionType?.ToLower().Contains("race") ?? false) ? 0 : -1);

                if (string.IsNullOrEmpty(t.SessionTypeFromYaml))
                    t.SessionTypeFromYaml = ses.SessionType ?? string.Empty;
            }
            else
            {
                t.TotalLaps = -1;
            }

            // --- Dados para cálculos ---
            t.LapDistTotal  = GetSdkValue<float>(d, "LapDist") ?? 0f;
            t.FuelUsedTotal = GetSdkValue<float>(d, "SessionFuelUsed") ?? 0f;

            // --- Cálculos personalizados ---
            try
            {
                t.FuelUsePerLapCalc = t.FuelUsePerLap;
                t.EstLapTimeCalc    = t.EstLapTime;

                double lapsLeftWithCurrentFuel = TelemetryCalculations.GetFuelLapsLeft(
                    t.FuelLevel,
                    t.FuelUsePerLap
                );
                t.LapsRemaining = (int)Math.Floor(lapsLeftWithCurrentFuel);

                t.ConsumoMedio = (t.Lap > 0 && t.FuelUsedTotal > 0) ? (t.FuelUsedTotal / t.Lap) : 0;
                t.VoltasRestantesMedio = (t.ConsumoMedio > 0) ? (t.FuelLevel / t.ConsumoMedio) : 0;

                if (t.TotalLaps > 0)
                {
                    t.LapsRemainingRace = t.TotalLaps - t.Lap;
                    if (t.LapsRemainingRace < 0) t.LapsRemainingRace = 0;
                }
                else
                {
                    t.LapsRemainingRace = (t.SessionTimeRemain > 0 && t.EstLapTime > 0 && t.EstLapTime < (60 * 20))
                        ? (int)Math.Floor(t.SessionTimeRemain / t.EstLapTime)
                        : 0;
                }

                float fuelNeededForRaceLaps = (t.LapsRemainingRace > 0 && t.FuelUsePerLap > 0)
                    ? (t.LapsRemainingRace * t.FuelUsePerLap) : 0;
                t.NecessarioFim = Math.Max(0, fuelNeededForRaceLaps - t.FuelLevel);

                float safetyMarginLiters = (t.FuelUsePerLap > 0) ? (t.FuelUsePerLap * 2) : 1.0f;
                t.RecomendacaoAbastecimento = Math.Max(0, t.NecessarioFim + safetyMarginLiters);

                t.FuelRemaining = t.FuelLevel;
                t.FuelEta       = t.LapsRemaining * t.EstLapTime;

                if (t.FuelLevel <= 0)
                {
                    t.FuelStatus = new FuelStatus { Text = "VAZIO", Class = "status-danger" };
                }
                else if (t.LapsRemaining <= 1)
                {
                    t.FuelStatus = new FuelStatus { Text = "PERIGO", Class = "status-danger" };
                }
                else if (t.LapsRemaining < 5)
                {
                    t.FuelStatus = new FuelStatus { Text = "ALERTA", Class = "status-warning" };
                }
                else
                {
                    t.FuelStatus = new FuelStatus { Text = "OK", Class = "status-ok" };
                }
            }
            catch (Exception calcEx)
            {
                _log.LogWarning(calcEx, "Falha ao executar cálculos em TelemetryCalculations.");
                t.LapsRemaining = 0;
                t.ConsumoMedio = 0;
                t.VoltasRestantesMedio = 0;
                t.LapsRemainingRace = 0;
                t.NecessarioFim = 0;
                t.RecomendacaoAbastecimento = 0;
                t.FuelStatus = new FuelStatus { Text = "ERRO", Class = "status-danger" };
            }

            return t;
        }
    }
}
