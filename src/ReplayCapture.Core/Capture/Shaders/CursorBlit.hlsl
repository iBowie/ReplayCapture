// Textured, alpha-blended quad used to draw the cursor sprite onto the captured frame. Kept as a
// pure GPU blend (BlendState does SrcAlpha/InvSrcAlpha) rather than the CPU-readback approach tried
// first: mapping a just-copied region for CPU read/write, even one tick later, still serialized
// against the encoder's own GPU work through the shared, driver-protected ID3D11DeviceContext and
// measured 150+ late pacer ticks in 20s at a 60fps target. A blend-only draw touches no CPU memory
// per frame and costs nothing next to the desktop copy already happening every tick.
struct VSInput
{
    float2 Pos : POSITION;
    float2 UV  : TEXCOORD0;
};

struct PSInput
{
    float4 Pos : SV_POSITION;
    float2 UV  : TEXCOORD0;
};

PSInput VSMain(VSInput input)
{
    PSInput output;
    output.Pos = float4(input.Pos, 0, 1);
    output.UV = input.UV;
    return output;
}

Texture2D CursorTex : register(t0);
SamplerState PointSampler : register(s0);

float4 PSMain(PSInput input) : SV_TARGET
{
    return CursorTex.Sample(PointSampler, input.UV);
}
