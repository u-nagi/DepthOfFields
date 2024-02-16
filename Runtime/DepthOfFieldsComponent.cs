using UnityEngine;

namespace Unagi.Dofs
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    public class DepthOfFieldsComponent : MonoBehaviour
    {
        [SerializeField] bool m_Active = true;

        [Space]

        [SerializeField] bool m_AutoFocus = false;
        [SerializeField] Transform m_FocusTransform = null;

        [Space]

        [Range(2, 8)]
        [SerializeField] int m_DownSample = 2;

        [Space]

        [Range(0.1f, 300f)]
        [SerializeField] float m_FocusDistance = 10f;
        [Range(1f, 32f)]
        [SerializeField] float m_Aperture = 5.6f;

        [Range(3f, 9f)]
        [SerializeField] float m_BladeCount = 5.0f;
        [Range(0f, 1f)]
        [SerializeField] float m_BladeCurvature = 1.0f;
        [Range(-180f, 180f)]
        [SerializeField] float m_BladeRotation = 0.0f;

        public bool active
        {
            get => m_Active;
            set => m_Active = value;
        }

        public bool autoFocus
        {
            get => m_AutoFocus;
            set => m_AutoFocus = value;
        }

        public Transform focusTransform
        {
            get => m_FocusTransform;
            set => m_FocusTransform = value;
        }

        public int downSample
        {
            get => m_DownSample;
            set => m_DownSample = value;
        }

        public float focusDistance
        {
            get => m_FocusDistance;
            set => m_FocusDistance = value;
        }

        public float aperture
        {
            get => m_Aperture;
            set => m_Aperture = value;
        }

        public float bladeCount
        {
            get => m_BladeCount;
            set => m_BladeCount = value;
        }

        public float bladeCurvature
        {
            get => m_BladeCurvature;
            set => m_BladeCurvature = value;
        }

        public float bladeRotation
        {
            get => m_BladeRotation;
            set => m_BladeRotation = value;
        }
    }
}
