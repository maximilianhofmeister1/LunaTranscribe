#!/usr/bin/env swift
// Zeichnet das App-Icon („Mondnacht“: Mondsichel, Wellenform, Sterne) mit CoreGraphics und schreibt
// alle Größen samt Contents.json nach LunaTranscribe/Resources/Assets.xcassets/AppIcon.appiconset.
// Aufruf aus dem Repo-Wurzelordner: swift scripts/make-icon.swift [--preview /tmp/icon.png]
import AppKit
import CoreGraphics
import ImageIO
import UniformTypeIdentifiers

// MARK: Palette (entspricht LunaColor in UI/Theme/Theme.swift)

func rgb(_ hex: UInt32, _ alpha: CGFloat = 1) -> CGColor {
    CGColor(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255, green: CGFloat((hex >> 8) & 0xFF) / 255,
            blue: CGFloat(hex & 0xFF) / 255, alpha: alpha)
}

let nightTop = rgb(0x3A2F6B)
let nightBottom = rgb(0x15132B)
let moonlight = rgb(0xFBF1DC)
let honey = rgb(0xE8B04B)
let lavender = rgb(0xB9A7F0)

// MARK: Zeichnen (Koordinaten im 1024er-Raster, Ursprung unten links)

func drawIcon(in ctx: CGContext) {
    // Apples Icon-Raster: 824 pt Körper, zentriert, Eckradius ~185.
    let body = CGRect(x: 100, y: 100, width: 824, height: 824)
    let shape = CGPath(roundedRect: body, cornerWidth: 185, cornerHeight: 185, transform: nil)

    // Schlagschatten des Körpers
    ctx.saveGState()
    ctx.setShadow(offset: CGSize(width: 0, height: -12), blur: 28, color: rgb(0x000000, 0.35))
    ctx.addPath(shape)
    ctx.setFillColor(nightBottom)
    ctx.fillPath()
    ctx.restoreGState()

    ctx.saveGState()
    ctx.addPath(shape)
    ctx.clip()

    // Nachthimmel
    let sky = CGGradient(colorsSpace: nil, colors: [nightTop, nightBottom] as CFArray, locations: [0, 1])!
    ctx.drawLinearGradient(sky, start: CGPoint(x: 0, y: body.maxY), end: CGPoint(x: 0, y: body.minY), options: [])

    // Warmer Schein hinter dem Mond
    let glowCenter = CGPoint(x: 440, y: 520)
    let glow = CGGradient(colorsSpace: nil, colors: [honey.copy(alpha: 0.45)!, honey.copy(alpha: 0)!] as CFArray,
                          locations: [0, 1])!
    ctx.drawRadialGradient(glow, startCenter: glowCenter, startRadius: 0, endCenter: glowCenter, endRadius: 430, options: [])

    // Sterne (vierzackig, verspielt verteilt)
    let stars: [(CGFloat, CGFloat, CGFloat, CGFloat)] = [
        (770, 850, 28, 0.95), (865, 700, 14, 0.7), (235, 815, 18, 0.75), (600, 885, 10, 0.6), (190, 300, 12, 0.5), (820, 250, 10, 0.45),
    ]
    for (x, y, r, a) in stars { drawSparkle(ctx, center: CGPoint(x: x, y: y), radius: r, color: moonlight.copy(alpha: a)!) }

    // Mondsichel: voller Kreis, aus dem ein versetzter Kreis herausgeschnitten wird. Die Transparenzebene
    // sorgt dafür, dass der Schein (Schatten) nur um die fertige Sichel entsteht.
    let moonCenter = CGPoint(x: 440, y: 520)
    let moonRadius: CGFloat = 255
    let hollowCenter = CGPoint(x: moonCenter.x + 150, y: moonCenter.y + 75)
    let hollowRadius: CGFloat = 215
    ctx.saveGState()
    ctx.setShadow(offset: .zero, blur: 46, color: honey.copy(alpha: 0.6))
    ctx.beginTransparencyLayer(auxiliaryInfo: nil)
    ctx.setFillColor(moonlight)
    ctx.fillEllipse(in: CGRect(x: moonCenter.x - moonRadius, y: moonCenter.y - moonRadius,
                               width: moonRadius * 2, height: moonRadius * 2))
    ctx.setBlendMode(.clear)
    ctx.fillEllipse(in: CGRect(x: hollowCenter.x - hollowRadius, y: hollowCenter.y - hollowRadius,
                               width: hollowRadius * 2, height: hollowRadius * 2))
    ctx.endTransparencyLayer()
    ctx.restoreGState()

    // Wellenform in der Aussparung der Sichel – der Mond „spricht“
    let bars: [CGFloat] = [64, 140, 220, 120, 176, 84]
    let barWidth: CGFloat = 30, gap: CGFloat = 20
    let totalWidth = CGFloat(bars.count) * barWidth + CGFloat(bars.count - 1) * gap
    var x = hollowCenter.x - totalWidth / 2 + 20
    let barGradient = CGGradient(colorsSpace: nil, colors: [honey, lavender] as CFArray, locations: [0, 1])!
    for height in bars {
        let rect = CGRect(x: x, y: hollowCenter.y - height / 2, width: barWidth, height: height)
        ctx.saveGState()
        ctx.addPath(CGPath(roundedRect: rect, cornerWidth: barWidth / 2, cornerHeight: barWidth / 2, transform: nil))
        ctx.clip()
        ctx.drawLinearGradient(barGradient, start: CGPoint(x: 0, y: rect.minY), end: CGPoint(x: 0, y: rect.maxY), options: [])
        ctx.restoreGState()
        x += barWidth + gap
    }

    // Leichter Glanz oben
    let sheen = CGGradient(colorsSpace: nil, colors: [rgb(0xFFFFFF, 0.10), rgb(0xFFFFFF, 0)] as CFArray, locations: [0, 1])!
    ctx.drawLinearGradient(sheen, start: CGPoint(x: 0, y: body.maxY), end: CGPoint(x: 0, y: body.midY), options: [])
    ctx.restoreGState()
}

func drawSparkle(_ ctx: CGContext, center c: CGPoint, radius r: CGFloat, color: CGColor) {
    let inner = r * 0.28
    let path = CGMutablePath()
    path.move(to: CGPoint(x: c.x, y: c.y + r))
    path.addQuadCurve(to: CGPoint(x: c.x + r, y: c.y), control: CGPoint(x: c.x + inner, y: c.y + inner))
    path.addQuadCurve(to: CGPoint(x: c.x, y: c.y - r), control: CGPoint(x: c.x + inner, y: c.y - inner))
    path.addQuadCurve(to: CGPoint(x: c.x - r, y: c.y), control: CGPoint(x: c.x - inner, y: c.y - inner))
    path.addQuadCurve(to: CGPoint(x: c.x, y: c.y + r), control: CGPoint(x: c.x - inner, y: c.y + inner))
    ctx.addPath(path)
    ctx.setFillColor(color)
    ctx.fillPath()
}

// MARK: Ausgabe

func renderPNG(pixels: Int, to url: URL) throws {
    let ctx = CGContext(data: nil, width: pixels, height: pixels, bitsPerComponent: 8, bytesPerRow: 0,
                        space: CGColorSpace(name: CGColorSpace.sRGB)!,
                        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
    ctx.interpolationQuality = .high
    ctx.scaleBy(x: CGFloat(pixels) / 1024, y: CGFloat(pixels) / 1024)
    drawIcon(in: ctx)
    let image = ctx.makeImage()!
    let dest = CGImageDestinationCreateWithURL(url as CFURL, UTType.png.identifier as CFString, 1, nil)!
    CGImageDestinationAddImage(dest, image, nil)
    guard CGImageDestinationFinalize(dest) else { throw NSError(domain: "make-icon", code: 1) }
}

let args = CommandLine.arguments
if let i = args.firstIndex(of: "--preview"), i + 1 < args.count {
    try renderPNG(pixels: 1024, to: URL(fileURLWithPath: args[i + 1]))
    print("Vorschau: \(args[i + 1])")
    exit(0)
}

let folder = URL(fileURLWithPath: "LunaTranscribe/Resources/Assets.xcassets/AppIcon.appiconset")
try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
var images: [[String: String]] = []
for points in [16, 32, 128, 256, 512] {
    for scale in [1, 2] {
        let name = "icon_\(points)x\(points)\(scale == 2 ? "@2x" : "").png"
        try renderPNG(pixels: points * scale, to: folder.appendingPathComponent(name))
        images.append(["idiom": "mac", "size": "\(points)x\(points)", "scale": "\(scale)x", "filename": name])
    }
}
let contents: [String: Any] = ["images": images, "info": ["author": "xcode", "version": 1]]
let json = try JSONSerialization.data(withJSONObject: contents, options: [.prettyPrinted, .sortedKeys])
try json.write(to: folder.appendingPathComponent("Contents.json"))
print("AppIcon geschrieben: \(folder.path)")
