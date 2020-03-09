(*
MIT License

Copyright (c) 2016 Alice Ryhl

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*)

type C = System.Numerics.Complex
open System
open System.Drawing

let hsltorgb (h,s,l) =
  let ftic f = 255. * f |> int |> min 255 |> max 0
  let C = (1.-abs (2.*l-1.)) * s
  let H = (h+3.14159265359) * 0.95492965855
  let X = C * (1. - abs ((H - 2.*(floor (H/2.))) - 1.))
  let (r1,g1,b1) = match (floor H |> int) with
                   | 0 -> (C,  X,  0.)
                   | 1 -> (X,  C,  0.)
                   | 2 -> (0., C,  X)
                   | 3 -> (0., X,  C)
                   | 4 -> (X,  0., C)
                   | 5 -> (C,  0., X)
                   | _ -> (0., 0., 0.)
  let m = l - C/2.
  (r1 + m |> ftic, g1 + m |> ftic, b1 + m |> ftic)

(* pick the region of the complex plane to show *)
let width = 512
let height = 512
let center = new C(0.0, 0.0)
let scale = 256.0 (* how many pixels per unit *)

(* pick a complex number from the pixel coordinate *)
let tc x w = (float x - 0.5*float w) / scale
let transform (x,y) = center + new C(tc x width, tc y height)

(* the function and its derivative *)
let f x = x*x*x - new C(1.0, 0.0)
let fd x = new C(3.0, 0.0) * x*x

(* computes one iteration *)
let step z = z - (f z) / (fd z)

(* computes the complex value to be shown in a pixel *)
let compute_pixel (z:C) =
  let rec aux z n =
    let z2 = step z
    if n > 100 then (z2,0.0) else
      let (zf,sum) = aux z2 (n+1)
      let delta = (z - zf).Magnitude
      (zf, sum + 1.0 - 1./(1.+exp (delta+3.5)-exp 3.5))
  aux z 0

(* incorporate the amount of iterations in the color *)
let complex_to_color (z:C,n) =
  let (r,g,b) = hsltorgb (z.Phase, 0.75, 0.5 / (1.+0.18*n))
  Color.FromArgb(r, g, b)

(* functions to compute the color of a pixel *)
let newton z = compute_pixel z
let newton_pixel x y = transform (x,y) |> newton |> complex_to_color

(* create an image *)
let bitmap = new Bitmap(width, height)
for px = 0 to width-1 do
  for py = 0 to height-1 do
    bitmap.SetPixel(px, py, newton_pixel px py)
bitmap.Save("fractal.png", System.Drawing.Imaging.ImageFormat.Png)
bitmap.Dispose

