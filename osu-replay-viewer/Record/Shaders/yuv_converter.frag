#version 300 es
precision highp float;

uniform sampler2D uTexture;
uniform vec2 uResolution; // Width, Height of the source image
uniform int uPixelFormat; // 0=YUV420, 1=YUV444, 2=NV12
uniform int uColorSpace;  // 0=BT601, 1=BT709

out float fragColor;

// BT.601 coefficients (SD)
const vec3 BT601_Y = vec3(0.299, 0.587, 0.114);
const vec3 BT601_U = vec3(-0.1687, -0.3313, 0.5);
const vec3 BT601_V = vec3(0.5, -0.4187, -0.0813);

// BT.709 coefficients (HD)
const vec3 BT709_Y = vec3(0.2126, 0.7152, 0.0722);
const vec3 BT709_U = vec3(-0.1146, -0.3854, 0.5);
const vec3 BT709_V = vec3(0.5, -0.4542, -0.0458);

float getY(vec3 rgb) {
    if (uColorSpace == 0) {
        return dot(rgb, BT601_Y);
    } else {
        return dot(rgb, BT709_Y);
    }
}

float getU(vec3 rgb) {
    if (uColorSpace == 0) {
        return dot(rgb, BT601_U) + 0.5;
    } else {
        return dot(rgb, BT709_U) + 0.5;
    }
}

float getV(vec3 rgb) {
    if (uColorSpace == 0) {
        return dot(rgb, BT601_V) + 0.5;
    } else {
        return dot(rgb, BT709_V) + 0.5;
    }
}

vec3 sampleRGB(float srcX, float srcY, float W, float H) {
    vec2 srcCoord = vec2((srcX + 0.5) / W, (srcY + 0.5) / H);
    return texture(uTexture, srcCoord).rgb;
}

void main() {
    float H = uResolution.y;
    float W = uResolution.x;

    float destX = gl_FragCoord.x - 0.5;
    float destY = gl_FragCoord.y - 0.5;

    vec3 rgb;

    if (uPixelFormat == 1) {
        // YUV444: Width x Height*3
        // Layout: Y plane (0 to H-1), U plane (H to 2H-1), V plane (2H to 3H-1)
        float planeIndex = floor(destY / H);
        float localY = mod(destY, H);

        // Flip vertically within each plane
        float srcY = (H - 1.0) - localY;
        rgb = sampleRGB(destX, srcY, W, H);

        if (planeIndex < 1.0) {
            fragColor = getY(rgb);
        } else if (planeIndex < 2.0) {
            fragColor = getU(rgb);
        } else {
            fragColor = getV(rgb);
        }

    } else if (uPixelFormat == 2) {
        // NV12: Width x Height*1.5
        // Layout: Y plane (0 to H-1), UV plane interleaved (H to 1.5H-1)
        if (destY < H) {
            // Y plane - full resolution
            float srcY = (H - 1.0) - destY;
            rgb = sampleRGB(destX, srcY, W, H);
            fragColor = getY(rgb);
        } else {
            // UV plane - interleaved U and V, 4:2:0 subsampling
            float uvY = destY - H;
            float localRow = floor(uvY);

            // Determine U or V based on column (even=U, odd=V)
            bool isU = mod(destX, 2.0) < 1.0;

            // Map to source coordinates (4:2:0 subsampling)
            float srcX = floor(destX / 2.0) * 2.0 + 0.5;
            float srcY = (H - 1.0) - (localRow * 2.0);

            rgb = sampleRGB(srcX, srcY, W, H);

            if (isU) {
                fragColor = getU(rgb);
            } else {
                fragColor = getV(rgb);
            }
        }

    } else {
        // YUV420 (default): Width x Height*1.5
        // Layout: Y plane (0 to H-1), U plane (H to 1.25H-1), V plane (1.25H to 1.5H-1)
        if (destY < H) {
            // Y plane - full resolution
            float srcY = (H - 1.0) - destY;
            rgb = sampleRGB(destX, srcY, W, H);
            fragColor = getY(rgb);
        } else {
            // U or V plane - 4:2:0 subsampling
            float uvY = destY - H;
            bool isU = uvY < (H / 4.0);
            float sectionY = isU ? uvY : (uvY - (H / 4.0));

            float logical_row = floor(sectionY);

            // Interleaved row mapping
            float src_row_offset = logical_row * 2.0;
            if (destX >= (W / 2.0)) {
                src_row_offset += 1.0;
            }

            float srcY = (H - 1.0) - (src_row_offset * 2.0);
            float srcX_pixel = (destX >= (W / 2.0)) ? (destX - (W / 2.0)) : destX;
            float srcX = srcX_pixel * 2.0;

            rgb = sampleRGB(srcX, srcY, W, H);

            if (isU) {
                fragColor = getU(rgb);
            } else {
                fragColor = getV(rgb);
            }
        }
    }
}
