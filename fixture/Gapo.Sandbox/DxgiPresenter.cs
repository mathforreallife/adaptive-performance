using System;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Gapo.Sandbox
{
    public sealed unsafe class DxgiPresenter : IDisposable
    {
        const int SwapChainBufferCount = 2;
        const int ConstantBufferBytes = 16;
        const uint MicrosoftVendorId = 0x1414;
        const uint BasicRenderDriverDeviceId = 0x8C;
        const string ShaderName = "sandbox-present.hlsl";
        const string ShaderSource = @"
Texture2D frameTexture : register(t0);
SamplerState frameSampler : register(s0);

cbuffer PixelLoad : register(b0)
{
    int iterations;
    int unusedA;
    int unusedB;
    int unusedC;
};

struct VertexOutput
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

VertexOutput VertexMain(uint vertexId : SV_VertexID)
{
    VertexOutput output;
    output.uv = float2((vertexId << 1) & 2, vertexId & 2);
    output.position = float4(output.uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return output;
}

float4 PixelMain(VertexOutput input) : SV_Target
{
    float3 colour = frameTexture.Sample(frameSampler, input.uv).rgb;
    float load = 0.0;
    [loop]
    for (int step = 0; step < iterations; step++)
    {
        load += sin(input.uv.x * (step + 1.0)) * cos(input.uv.y * (step + 1.0));
    }
    return float4(colour + load * 0.0000001, 1.0);
}";

        [StructLayout(LayoutKind.Sequential, Size = ConstantBufferBytes)]
        struct PixelLoadConstants
        {
            public int Iterations;
        }

        ID3D11Device _device;
        ID3D11DeviceContext _context;
        IDXGISwapChain1 _swapChain;
        ID3D11RenderTargetView _backBufferView;
        ID3D11Texture2D _frameTexture;
        ID3D11ShaderResourceView _frameView;
        ID3D11VertexShader _vertexShader;
        ID3D11PixelShader _pixelShader;
        ID3D11SamplerState _sampler;
        ID3D11Buffer _loadBuffer;
        int _width, _height;
        int _appliedIterations = -1;

        public string AdapterName { get; private set; }
        public bool IsSoftwareAdapter { get; private set; }

        DxgiPresenter() { }

        public static DxgiPresenter TryCreate(IntPtr window, int width, int height, out string failure)
        {
            var presenter = new DxgiPresenter();
            try
            {
                presenter.Initialise(window, width, height);
                failure = null;
                return presenter;
            }
            catch (Exception exception)
            {
                presenter.Dispose();
                failure = exception.GetType().Name + ": " + exception.Message;
                return null;
            }
        }

        void Initialise(IntPtr window, int width, int height)
        {
            CreateDevice();
            using (var dxgiDevice = _device.QueryInterface<IDXGIDevice>())
            using (var adapter = dxgiDevice.GetAdapter())
            using (var factory = adapter.GetParent<IDXGIFactory2>())
            {
                var adapterDescription = adapter.Description;
                AdapterName = adapterDescription.Description;
                if (adapterDescription.VendorId == MicrosoftVendorId && adapterDescription.DeviceId == BasicRenderDriverDeviceId) IsSoftwareAdapter = true;
                var description = new SwapChainDescription1
                {
                    Width = (uint)width,
                    Height = (uint)height,
                    Format = Format.B8G8R8A8_UNorm,
                    BufferCount = SwapChainBufferCount,
                    BufferUsage = Usage.RenderTargetOutput,
                    SampleDescription = new SampleDescription(1, 0),
                    Scaling = Scaling.Stretch,
                    SwapEffect = SwapEffect.FlipDiscard,
                    AlphaMode = AlphaMode.Ignore,
                };
                _swapChain = factory.CreateSwapChainForHwnd(_device, window, description);
                factory.MakeWindowAssociation(window, WindowAssociationFlags.IgnoreAltEnter);
            }

            Compiler.Compile(ShaderSource, "VertexMain", ShaderName, "vs_4_0", out Blob vertexCode, out Blob vertexErrors).CheckError();
            Compiler.Compile(ShaderSource, "PixelMain", ShaderName, "ps_4_0", out Blob pixelCode, out Blob pixelErrors).CheckError();
            _vertexShader = _device.CreateVertexShader(vertexCode.AsSpan());
            _pixelShader = _device.CreatePixelShader(pixelCode.AsSpan());
            vertexCode.Dispose();
            pixelCode.Dispose();
            vertexErrors?.Dispose();
            pixelErrors?.Dispose();

            _sampler = _device.CreateSamplerState(SamplerDescription.PointClamp);
            _loadBuffer = _device.CreateBuffer((uint)sizeof(PixelLoadConstants), BindFlags.ConstantBuffer);
            CreateSizeDependentResources(width, height);
        }

        void CreateDevice()
        {
            var featureLevels = new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
            var result = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, featureLevels, out _device, out _context);
            if (result.Failure)
            {
                D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport, featureLevels, out _device, out _context).CheckError();
                IsSoftwareAdapter = true;
            }
        }

        void CreateSizeDependentResources(int width, int height)
        {
            _width = width;
            _height = height;
            using (var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0))
                _backBufferView = _device.CreateRenderTargetView(backBuffer);
            _frameTexture = _device.CreateTexture2D(new Texture2DDescription(
                Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1, BindFlags.ShaderResource, ResourceUsage.Dynamic, CpuAccessFlags.Write));
            _frameView = _device.CreateShaderResourceView(_frameTexture);
        }

        void ReleaseSizeDependentResources()
        {
            _context.OMSetRenderTargets((ID3D11RenderTargetView)null);
            _frameView?.Dispose();
            _frameTexture?.Dispose();
            _backBufferView?.Dispose();
            _frameView = null;
            _frameTexture = null;
            _backBufferView = null;
        }

        public void Present(int* pixels, int width, int height, int pixelLoadIterations)
        {
            if (width != _width || height != _height)
            {
                ReleaseSizeDependentResources();
                _swapChain.ResizeBuffers(0, (uint)width, (uint)height, Format.Unknown, SwapChainFlags.None).CheckError();
                CreateSizeDependentResources(width, height);
            }

            if (pixelLoadIterations != _appliedIterations)
            {
                var constants = new PixelLoadConstants { Iterations = pixelLoadIterations };
                _context.UpdateSubresource(in constants, _loadBuffer);
                _appliedIterations = pixelLoadIterations;
            }

            var mapped = _context.Map(_frameTexture, 0, MapMode.WriteDiscard);
            long rowBytes = (long)width * sizeof(int);
            for (int y = 0; y < height; y++)
                Buffer.MemoryCopy(pixels + (long)y * width, (byte*)mapped.DataPointer + (long)y * mapped.RowPitch, rowBytes, rowBytes);
            _context.Unmap(_frameTexture, 0);

            _context.OMSetRenderTargets(_backBufferView);
            _context.RSSetViewport(0, 0, width, height);
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.VSSetShader(_vertexShader);
            _context.PSSetShader(_pixelShader);
            _context.PSSetShaderResource(0, _frameView);
            _context.PSSetSampler(0, _sampler);
            _context.PSSetConstantBuffer(0, _loadBuffer);
            _context.Draw(3, 0);
            _swapChain.Present(0, PresentFlags.None);
        }

        public void Dispose()
        {
            if (_context != null)
            {
                _context.ClearState();
                _context.Flush();
            }

            _frameView?.Dispose();
            _frameTexture?.Dispose();
            _backBufferView?.Dispose();
            _loadBuffer?.Dispose();
            _sampler?.Dispose();
            _pixelShader?.Dispose();
            _vertexShader?.Dispose();
            _swapChain?.Dispose();
            _context?.Dispose();
            _device?.Dispose();
            _context = null;
            _device = null;
        }
    }
}
