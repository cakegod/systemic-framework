namespace MoleculeSwarm;

/// <summary>The GLSL for every pass. Kept beside the batch that uses them so a pass is one file.</summary>
internal static class Shaders
{
    public const string TerrainVertex = """
        #version 330 core
        layout(location = 0) in vec2 aPosition;
        layout(location = 1) in vec2 aUv;
        uniform vec2 uViewport;
        uniform vec3 uCamera;
        out vec2 vUv;
        void main()
        {
            gl_Position = vec4(aPosition, 0.0, 1.0);
            // The quad always fills the window; the camera decides which part of the field it shows.
            vec2 world = uCamera.xy + ((aUv - 0.5) * uViewport / uCamera.z);
            vUv = world / uViewport;
        }
        """;

    public const string TerrainFragment = """
        #version 330 core
        in vec2 vUv;
        uniform sampler2D uImage;
        out vec4 fragColor;
        void main()
        {
            if (vUv.x < 0.0 || vUv.y < 0.0 || vUv.x > 1.0 || vUv.y > 1.0) {
                // Zoomed out past the edge of the field: there is nothing out there.
                fragColor = vec4(0.0, 0.0, 0.0, 1.0);
                return;
            }

            vec3 ground = texture(uImage, vUv).rgb;
            vec2 offset = vUv - 0.5;
            float vignette = 1.0 - dot(offset, offset) * 0.22;
            fragColor = vec4(ground * vignette, 1.0);
        }
        """;

    public const string ShapeVertex = """
        #version 330 core
        layout(location = 0) in vec2 aPosition;
        layout(location = 1) in vec4 aColor;
        uniform vec2 uViewport;
        uniform vec3 uCamera;
        out vec4 vColor;
        void main()
        {
            vec2 screen = ((aPosition - uCamera.xy) * uCamera.z) + (uViewport * 0.5);
            vec2 clip = vec2((screen.x / uViewport.x) * 2.0 - 1.0, 1.0 - (screen.y / uViewport.y) * 2.0);
            gl_Position = vec4(clip, 0.0, 1.0);
            vColor = aColor;
        }
        """;

    public const string ShapeFragment = """
        #version 330 core
        in vec4 vColor;
        out vec4 fragColor;
        void main()
        {
            fragColor = vColor;
        }
        """;

    public const string OrcVertex = """
        #version 330 core
        layout(location = 0) in vec2 aPosition;
        layout(location = 1) in float aKind;
        layout(location = 2) in float aSeed;
        uniform vec2 uViewport;
        uniform vec3 uCamera;
        uniform float uTime;
        flat out float vKind;
        flat out float vSeed;
        void main()
        {
            float bob = sin(uTime * 5.0 + aSeed * 39.0) * 0.7;
            vec2 point = aPosition + vec2(0.0, bob);
            vec2 screen = ((point - uCamera.xy) * uCamera.z) + (uViewport * 0.5);
            vec2 clip = vec2((screen.x / uViewport.x) * 2.0 - 1.0, 1.0 - (screen.y / uViewport.y) * 2.0);
            gl_Position = vec4(clip, 0.0, 1.0);
            // Drawn much wider than the body on purpose: the halo is the glow, not the body.
            float base = aKind < 0.5 ? 64.0 : (aKind < 1.5 ? 88.0 : 50.0);
            gl_PointSize = base * uCamera.z * (0.92 + fract(aSeed * 13.7) * 0.16);
            vKind = aKind;
            vSeed = aSeed;
        }
        """;

    public const string OrcFragment = """
        #version 330 core
        flat in float vKind;
        flat in float vSeed;
        out vec4 fragColor;
        void main()
        {
            vec2 p = gl_PointCoord * 2.0 - 1.0;
            float r = length(p);
            if (r > 1.0) {
                discard;
            }

            bool brute = vKind > 0.5 && vKind < 1.5;
            bool runner = vKind > 1.5;

            // The kind is the colour: grunts are a soft yellow, brutes a deep red, runners a mint
            // green. Each molecule keeps a little of its own variation around that hue, and the core
            // is the same hue lifted toward white rather than a white dot.
            vec3 body = brute
                ? vec3(1.00, 0.34, 0.30)
                : (runner ? vec3(0.50, 0.95, 0.58) : vec3(1.00, 0.85, 0.45));
            body *= 0.88 + (fract(vSeed * 7.31) * 0.24);
            vec3 coreColor = mix(body, vec3(1.0), 0.55);

            // A hot inner point, a rounded body, and a wide aura that bleeds into the dark. The aura
            // is broad and weak on purpose: it is added on top of the background, so a long falloff
            // reads as light around the molecule instead of a hard disc.
            float core = pow(smoothstep(0.20, 0.0, r), 1.4);
            float shell = smoothstep(0.34, 0.20, r);
            float aura = pow(max(0.0, 1.0 - r), 1.05);
            float bloom = pow(max(0.0, 1.0 - r), 3.2);
            vec3 color = (coreColor * core * 1.9) + (body * bloom * 0.6) + (body * aura * 0.45) +
                (body * shell * 0.3);

            // The ring of satellites that makes it read as a molecule rather than a dot.
            float angle = atan(p.y, p.x);
            float ringRadius = brute ? 0.34 : 0.30;
            float satellites = smoothstep(0.45, 0.95, cos((angle * (brute ? 9.0 : 8.0)) + (vSeed * 6.283)));
            float ring = smoothstep(0.05, 0.01, abs(r - ringRadius)) * satellites;
            color += mix(coreColor, body, 0.5) * ring * 0.85;

            // Brutes carry a second, wider shell so the heavy nuclei read as bigger.
            if (brute) {
                float outer = smoothstep(0.05, 0.01, abs(r - 0.52)) * satellites;
                color += body * outer * 0.8;
            }

            float alpha = max(max(aura * 0.26, shell * 0.5), max(core, ring));
            if (alpha < 0.02) {
                discard;
            }

            fragColor = vec4(color, min(alpha, 1.0));
        }
        """;

    public const string SplatVertex = """
        #version 330 core
        layout(location = 0) in vec2 aPosition;
        layout(location = 1) in float aRadius;
        layout(location = 2) in float aTint;
        uniform vec2 uViewport;
        uniform vec3 uCamera;
        flat out float vTint;
        void main()
        {
            vec2 screen = ((aPosition - uCamera.xy) * uCamera.z) + (uViewport * 0.5);
            vec2 clip = vec2((screen.x / uViewport.x) * 2.0 - 1.0, 1.0 - (screen.y / uViewport.y) * 2.0);
            gl_Position = vec4(clip, 0.0, 1.0);
            gl_PointSize = aRadius * 2.0 * uCamera.z;
            vTint = aTint;
        }
        """;

    public const string SplatFragment = """
        #version 330 core
        flat in float vTint;
        uniform vec4 uColor;
        out vec4 fragColor;
        void main()
        {
            vec2 p = gl_PointCoord * 2.0 - 1.0;
            float angle = atan(p.y, p.x);
            float wobble = 1.0 + 0.13 * sin(angle * 5.0 + vTint * 6.283) + 0.06 * sin(angle * 11.0 - vTint * 4.0);
            float r = length(p);
            if (r > wobble) {
                discard;
            }

            fragColor = vec4(uColor.rgb, uColor.a * smoothstep(wobble, wobble * 0.45, r));
        }
        """;

    /// <summary>Full-screen quad in clip space for the bloom's texture passes.</summary>
    public const string QuadVertex = """
        #version 330 core
        layout(location = 0) in vec2 aPosition;
        out vec2 vUv;
        void main()
        {
            vUv = (aPosition * 0.5) + 0.5;
            gl_Position = vec4(aPosition, 0.0, 1.0);
        }
        """;

    /// <summary>Keeps only what is bright enough to spill light, so the dirt does not bloom.</summary>
    public const string BrightFragment = """
        #version 330 core
        in vec2 vUv;
        uniform sampler2D uImage;
        uniform float uThreshold;
        out vec4 fragColor;
        void main()
        {
            vec3 color = texture(uImage, vUv).rgb;
            float luminance = dot(color, vec3(0.2126, 0.7152, 0.0722));
            float keep = max(0.0, luminance - uThreshold) / max(luminance, 0.0001);
            fragColor = vec4(color * keep, 1.0);
        }
        """;

    /// <summary>One direction of a five-tap Gaussian, run once per axis.</summary>
    public const string BlurFragment = """
        #version 330 core
        in vec2 vUv;
        uniform sampler2D uImage;
        uniform vec2 uDirection;
        out vec4 fragColor;
        void main()
        {
            vec3 sum = texture(uImage, vUv).rgb * 0.2270270270;
            sum += (texture(uImage, vUv + (uDirection * 1.3846153846)).rgb +
                texture(uImage, vUv - (uDirection * 1.3846153846)).rgb) * 0.3162162162;
            sum += (texture(uImage, vUv + (uDirection * 3.2307692308)).rgb +
                texture(uImage, vUv - (uDirection * 3.2307692308)).rgb) * 0.0702702703;
            fragColor = vec4(sum, 1.0);
        }
        """;

    /// <summary>The scene, plus the blurred brightness added on top of it.</summary>
    public const string BloomFragment = """
        #version 330 core
        in vec2 vUv;
        uniform sampler2D uScene;
        uniform sampler2D uGlow;
        uniform float uStrength;
        out vec4 fragColor;
        void main()
        {
            vec3 scene = texture(uScene, vUv).rgb;
            vec3 glow = texture(uGlow, vUv).rgb;
            fragColor = vec4(scene + (glow * uStrength), 1.0);
        }
        """;

    public const string GlowVertex = """
        #version 330 core
        layout(location = 0) in vec2 aPosition;
        layout(location = 1) in float aRadius;
        layout(location = 2) in float aAlpha;
        uniform vec2 uViewport;
        uniform vec3 uCamera;
        flat out float vAlpha;
        void main()
        {
            vec2 screen = ((aPosition - uCamera.xy) * uCamera.z) + (uViewport * 0.5);
            vec2 clip = vec2((screen.x / uViewport.x) * 2.0 - 1.0, 1.0 - (screen.y / uViewport.y) * 2.0);
            gl_Position = vec4(clip, 0.0, 1.0);
            gl_PointSize = min(aRadius * 2.0 * uCamera.z, 200.0);
            vAlpha = aAlpha;
        }
        """;

    public const string GlowFragment = """
        #version 330 core
        flat in float vAlpha;
        out vec4 fragColor;
        void main()
        {
            float r = length(gl_PointCoord * 2.0 - 1.0);
            if (r > 1.0) {
                discard;
            }

            float falloff = pow(1.0 - r, 2.2);
            fragColor = vec4(1.0, 0.86, 0.60, vAlpha * falloff);
        }
        """;

}
