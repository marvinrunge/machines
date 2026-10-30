shader_type canvas_item;

// The menus' circuit board (tools/blender/menu-art.py), repeated over the window. Now and then a bus sends a signal:
// a pulse of light runs along all its traces from their start to their end (board_traces.png: red where a trace
// lies, green how far along it, blue which bus). Soot and shade darken it in patches and towards the edges, as on
// the original's boards.
uniform sampler2D board : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D traces : filter_linear, repeat_enable;
uniform vec2 size = vec2(1920.0, 1080.0);   // the board's size in menu pixels
uniform float tile = 1365.0;                // one tile's size in menu pixels
uniform vec3 tint : source_color = vec3(1.0);
uniform vec3 pulse : source_color = vec3(0.55, 1.0, 0.3);

float hash(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

float noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), u.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), u.x), u.y);
}

float fbm(vec2 p) {
	float value = 0.0;
	float amount = 0.5;
	for (int i = 0; i < 5; i++) {
		value += amount * noise(p);
		p *= 2.03;
		amount *= 0.5;
	}
	return value;
}

void fragment() {
	vec2 px = UV * size;
	vec2 uv = px / tile;
	vec3 colour = texture(board, uv).rgb * tint;
	vec3 trace = texture(traces, uv).rgb;

	// Each bus has its own beat; on one beat in three it fires, the pulse running from the traces' start to their end.
	float period = 4.0 + trace.b * 7.0;
	float beat = TIME / period + trace.b * 17.0;
	float fires = step(0.66, hash(vec2(floor(beat), trace.b * 97.0)));
	float head = fract(beat) * 1.6 - 0.3;
	float behind = head - trace.g;
	float light = fires * trace.r * smoothstep(-0.012, 0.0, behind) * exp(-max(behind, 0.0) * 12.0);
	colour += pulse * light * 1.4;

	float soot = fbm(px / 430.0 + vec2(TIME * 0.004, 0.0));
	colour *= mix(0.5, 1.06, smoothstep(0.22, 0.78, soot));
	vec2 centred = (UV - 0.5) * vec2(size.x / size.y, 1.0);
	colour *= smoothstep(1.3, 0.3, length(centred));
	COLOR = vec4(colour, 1.0);
}
