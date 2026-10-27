using System;
using System.Collections.Generic;
using UnityEngine;
using AITraffic.Config;
using AITraffic.Compat;

namespace AITraffic.Fleet
{
    /// <summary>
    /// Standalone End-of-Train (EOT) marker / red flashing telemetry lantern.
    /// 
    /// CRITICAL REQUIREMENT:
    /// This is an independent visual GameObject / Light attached directly to the rear car's coupler/bumper.
    /// It NEVER touches, references, or interferes with player inventory or player lantern items.
    /// </summary>
    public class ConsistEOTMarker : MonoBehaviour
    {
        private static readonly Dictionary<TrainCar, ConsistEOTMarker> s_activeMarkers = new Dictionary<TrainCar, ConsistEOTMarker>();

        public TrainCar TargetCar { get; private set; }
        public Coupler MountedCoupler { get; private set; }

        public bool IsFlashing { get; set; }
        public float FlashFrequencyHz { get; set; }

        private void Awake()
        {
            IsFlashing = true;
            FlashFrequencyHz = 1.0f;
        }

        private Light _pointLight;
        private MeshRenderer _lensRenderer;
        private Material _lensMaterial;
        private Material _housingMaterial;
        private Material _bracketMaterial;

        private float _timer = 0f;
        private bool _isCurrentlyLit = false;

        private readonly Color _emissiveOn = new Color(1.0f, 0.06f, 0.06f, 1.0f);
        private readonly Color _emissiveOff = new Color(0.12f, 0.01f, 0.01f, 1.0f);

        /// <summary>
        /// Attaches or updates a standalone EOT marker lantern on the outer uncoupled coupler of the target car.
        /// </summary>
        public static ConsistEOTMarker AttachToRearCar(TrainCar car, bool flashing = true, float flashRate = 1.0f)
        {
            if (car == null || car.gameObject == null) return null;

            // Remove any existing marker if already attached to avoid duplicates
            ConsistEOTMarker existingMarker;
            if (s_activeMarkers.TryGetValue(car, out existingMarker) && existingMarker != null)
            {
                existingMarker.IsFlashing = flashing;
                existingMarker.FlashFrequencyHz = flashRate > 0f ? flashRate : 1.0f;
                return existingMarker;
            }

            // Determine which coupler is at the uncoupled exterior end
            Coupler targetCoupler = null;
            if (car.rearCoupler != null && !car.rearCoupler.IsCoupled())
            {
                targetCoupler = car.rearCoupler;
            }
            else if (car.frontCoupler != null && !car.frontCoupler.IsCoupled())
            {
                targetCoupler = car.frontCoupler;
            }
            else
            {
                targetCoupler = car.rearCoupler ?? car.frontCoupler;
            }

            if (targetCoupler == null) return null;

            // Create root GameObject attached directly to coupler transform
            var markerGo = new GameObject("ConsistEOTMarker_" + ModCompatManager.GetCarId(car));
            markerGo.transform.SetParent(targetCoupler.transform, false);

            // Positioned slightly above and offset from the coupler knuckle pocket
            markerGo.transform.localPosition = new Vector3(0.20f, 0.32f, 0.05f);
            markerGo.transform.localRotation = Quaternion.identity;

            var marker = markerGo.AddComponent<ConsistEOTMarker>();
            marker.Initialize(car, targetCoupler, flashing, flashRate);

            s_activeMarkers[car] = marker;
            return marker;
        }

        /// <summary>
        /// Removes and cleans up any active EOT marker on the given train car.
        /// </summary>
        public static void RemoveFromCar(TrainCar car)
        {
            if (car == null) return;

            ConsistEOTMarker marker;
            if (s_activeMarkers.TryGetValue(car, out marker) && marker != null)
            {
                s_activeMarkers.Remove(car);
                if (marker.gameObject != null)
                {
                    Destroy(marker.gameObject);
                }
            }
            else
            {
                s_activeMarkers.Remove(car);
            }
        }

        /// <summary>
        /// Retrieves the active EOT marker on the given train car if present.
        /// </summary>
        public static ConsistEOTMarker GetMarker(TrainCar car)
        {
            if (car == null) return null;
            ConsistEOTMarker marker;
            s_activeMarkers.TryGetValue(car, out marker);
            return marker;
        }

        /// <summary>
        /// Cleans up all active EOT markers across the world.
        /// </summary>
        public static void RemoveAll()
        {
            foreach (var kvp in s_activeMarkers)
            {
                if (kvp.Value != null && kvp.Value.gameObject != null)
                {
                    Destroy(kvp.Value.gameObject);
                }
            }
            s_activeMarkers.Clear();
        }

        private void Initialize(TrainCar car, Coupler coupler, bool flashing, float flashRate)
        {
            TargetCar = car;
            MountedCoupler = coupler;
            IsFlashing = flashing;
            FlashFrequencyHz = flashRate > 0f ? flashRate : 1.0f;

            BuildVisualHierarchy();
            SetIllumination(true);
        }

        private void BuildVisualHierarchy()
        {
            Shader standardShader = Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse");

            // 1. Steel Mounting Bracket / Clamp
            var bracket = GameObject.CreatePrimitive(PrimitiveType.Cube);
            bracket.name = "EOT_Bracket";
            bracket.transform.SetParent(transform, false);
            bracket.transform.localPosition = new Vector3(0f, -0.08f, -0.02f);
            bracket.transform.localScale = new Vector3(0.08f, 0.12f, 0.08f);
            var bracketCol = bracket.GetComponent<Collider>();
            if (bracketCol != null) Destroy(bracketCol);

            _bracketMaterial = new Material(standardShader);
            _bracketMaterial.color = new Color(0.18f, 0.18f, 0.20f);
            var bracketRend = bracket.GetComponent<MeshRenderer>();
            if (bracketRend != null) bracketRend.material = _bracketMaterial;

            // 2. High-Visibility Safety Housing (Hazard Yellow/Orange casing)
            var housing = GameObject.CreatePrimitive(PrimitiveType.Cube);
            housing.name = "EOT_Housing";
            housing.transform.SetParent(transform, false);
            housing.transform.localPosition = new Vector3(0f, 0.04f, 0f);
            housing.transform.localScale = new Vector3(0.15f, 0.22f, 0.15f);
            var housingCol = housing.GetComponent<Collider>();
            if (housingCol != null) Destroy(housingCol);

            _housingMaterial = new Material(standardShader);
            _housingMaterial.color = new Color(0.95f, 0.58f, 0.05f); // High-vis railroad warning yellow/orange
            var housingRend = housing.GetComponent<MeshRenderer>();
            if (housingRend != null) housingRend.material = _housingMaterial;

            // 3. Red Fresnel Optical Lens (Oriented facing backward along coupler outward axis)
            var lens = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            lens.name = "EOT_Lens";
            lens.transform.SetParent(transform, false);
            lens.transform.localPosition = new Vector3(0f, 0.06f, 0.08f);
            lens.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            lens.transform.localScale = new Vector3(0.09f, 0.02f, 0.09f);
            var lensCol = lens.GetComponent<Collider>();
            if (lensCol != null) Destroy(lensCol);

            _lensMaterial = new Material(standardShader);
            _lensMaterial.color = Color.red;
            _lensMaterial.EnableKeyword("_EMISSION");
            _lensMaterial.SetColor("_EmissionColor", _emissiveOn);
            _lensRenderer = lens.GetComponent<MeshRenderer>();
            if (_lensRenderer != null) _lensRenderer.material = _lensMaterial;

            // 4. Standalone Red Point Light Source
            var lightObj = new GameObject("EOT_PointLight");
            lightObj.transform.SetParent(transform, false);
            lightObj.transform.localPosition = new Vector3(0f, 0.06f, 0.14f);

            _pointLight = lightObj.AddComponent<Light>();
            _pointLight.type = LightType.Point;
            _pointLight.color = new Color(1.0f, 0.12f, 0.12f);
            _pointLight.range = 8.5f;
            _pointLight.intensity = 2.2f;
            _pointLight.renderMode = LightRenderMode.ForcePixel;
            _pointLight.cullingMask = ~0; // illuminate world
        }

        private void Update()
        {
            // Verify host car integrity
            if (TargetCar == null || TargetCar.gameObject == null)
            {
                Destroy(gameObject);
                return;
            }

            // If the coupler has been coupled to another car mid-session, automatically detach
            if (MountedCoupler != null && MountedCoupler.IsCoupled())
            {
                RemoveFromCar(TargetCar);
                return;
            }

            if (IsFlashing)
            {
                _timer += Time.deltaTime;
                float period = FlashFrequencyHz > 0f ? (1.0f / FlashFrequencyHz) : 1.0f;
                // Standard FRA / railway flashing marker: ~0.45s ON pulse per cycle (~1 Hz)
                bool shouldBeLit = (_timer % period) < (period * 0.45f);
                if (shouldBeLit != _isCurrentlyLit)
                {
                    SetIllumination(shouldBeLit);
                }
            }
            else
            {
                if (!_isCurrentlyLit)
                {
                    SetIllumination(true);
                }
            }
        }

        private void SetIllumination(bool lit)
        {
            _isCurrentlyLit = lit;

            if (_pointLight != null)
            {
                _pointLight.enabled = lit;
            }

            if (_lensRenderer != null && _lensMaterial != null)
            {
                Color emission = lit ? _emissiveOn : _emissiveOff;
                _lensMaterial.SetColor("_EmissionColor", emission);
                DynamicGI.SetEmissive(_lensRenderer, emission);
            }
        }

        private void OnDestroy()
        {
            ConsistEOTMarker marker;
            if (TargetCar != null && s_activeMarkers.TryGetValue(TargetCar, out marker) && marker == this)
            {
                s_activeMarkers.Remove(TargetCar);
            }

            if (_lensMaterial != null) Destroy(_lensMaterial);
            if (_housingMaterial != null) Destroy(_housingMaterial);
            if (_bracketMaterial != null) Destroy(_bracketMaterial);
        }
    }
}
