#version 300 es
precision highp float;

uniform sampler2D uTexture;
uniform vec2 uResolution; // Width, Height of the source image

out float fragColor;

void main() {
    float H = uResolution.y;
    float W = uResolution.x;
    
    // gl_FragCoord is in pixels, (0.5, 0.5) is bottom-left pixel center.
    float destX = gl_FragCoord.x - 0.5;
    float destY = gl_FragCoord.y - 0.5;
    
    vec3 rgb;
    
    if (destY < H) {
        // Y Plane
        // Map FBO bottom (0) to Source Top (H-1)
        // srcY = (H - 1) - destY
        float srcY = (H - 1.0) - destY;
        
        vec2 srcCoord = vec2((destX + 0.5) / W, (srcY + 0.5) / H);
        rgb = texture(uTexture, srcCoord).rgb;
        
        // Y
        fragColor = 0.2126 * rgb.r + 0.7152 * rgb.g + 0.0722 * rgb.b;
        
    } else {
        // U or V Plane
        float uvY = destY - H; // [0, H/2 - 1]
        
        bool isU = uvY < (H / 4.0);
        float sectionY = isU ? uvY : (uvY - (H / 4.0));
        
        // Map sectionY to Source Y lines.
        // logical_row = floor(sectionY)
        float logical_row = floor(sectionY);
        
        // Source row offset from Top
        float src_row_offset = logical_row * 2.0;
        if (destX >= (W / 2.0)) {
            src_row_offset += 1.0;
        }
        
        // Map to Source Y coordinate (from bottom)
        // Top is H-1.
        // srcY = (H - 1) - src_row_offset
        float srcY = (H - 1.0) - (src_row_offset * 2.0);
        
        // Map X
        float srcX_pixel = (destX >= (W / 2.0)) ? (destX - (W / 2.0)) : destX;
        // Multiply by 2 to map to source width
        float srcX = srcX_pixel * 2.0;
        
        vec2 srcCoord = vec2((srcX + 0.5) / W, (srcY + 0.5) / H);
        rgb = texture(uTexture, srcCoord).rgb;
        
        if (isU) {
            // U
            fragColor = -0.1146 * rgb.r - 0.3854 * rgb.g + 0.5000 * rgb.b + 0.5;
        } else {
            // V
            fragColor = 0.5000 * rgb.r - 0.4542 * rgb.g - 0.0458 * rgb.b + 0.5;
        }
    }
}
