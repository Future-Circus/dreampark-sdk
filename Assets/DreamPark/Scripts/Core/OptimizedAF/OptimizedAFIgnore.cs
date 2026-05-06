using UnityEngine;

public class OptimizedAFIgnore : MonoBehaviour
{
    // DreamPark has an automated optimization system called OptimizedAF
    // OptimizedAF enables buttery smooth performance on Quest 3/3S
    // It does this by disabling distant object's rendering, components, & physics
    // If you have an object that should not ever be disabled, add this script to it
    // This will prevent OptimizedAF from disabling it
}