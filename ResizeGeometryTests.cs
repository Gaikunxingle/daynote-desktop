using System;
using Daynote;

public static class ResizeGeometryTests
{
    static int assertions;
    static void Check(bool value, string name) { assertions++; if (!value) throw new Exception(name); }
    static void Near(double actual, double expected, string name) { Check(Math.Abs(actual-expected)<.00001,name); }
    static PixelRect Rect(int left,int top,int right,int bottom) { return new PixelRect{Left=left,Top=top,Right=right,Bottom=bottom}; }
    static DisplayMonitor Monitor(PixelRect work,uint dpi) { return new DisplayMonitor{Work=work,Bounds=work,DpiX=dpi,DpiY=dpi,Device="fixture"}; }
    public static int Main()
    {
        try
        {
            var monitor=Monitor(Rect(0,0,2560,1392),96);
            var original=Rect(300,200,920,760);
            var left=DisplayLayout.Resize(monitor,original,-1,0,-80,999,560,420,0);
            Check(left.Rect.Left==220&&left.Rect.Right==920&&left.Rect.Top==200&&left.Rect.Bottom==760,"left preserves opposite edge and vertical axis");
            Near(left.WidthDip,700,"left width");
            var right=DisplayLayout.Resize(monitor,original,1,0,80,999,560,420,0);
            Check(right.Rect.Left==300&&right.Rect.Right==1000&&right.Rect.Top==200&&right.Rect.Bottom==760,"right preserves opposite edge and vertical axis");
            var top=DisplayLayout.Resize(monitor,original,0,-1,999,-50,560,420,0);
            Check(top.Rect.Top==150&&top.Rect.Bottom==760&&top.Rect.Left==300&&top.Rect.Right==920,"top preserves opposite edge and horizontal axis");
            var bottom=DisplayLayout.Resize(monitor,original,0,1,999,50,560,420,0);
            Check(bottom.Rect.Top==200&&bottom.Rect.Bottom==810&&bottom.Rect.Left==300&&bottom.Rect.Right==920,"bottom preserves opposite edge and horizontal axis");
            foreach(int x in new[]{-1,1}) foreach(int y in new[]{-1,1}) {
                var corner=DisplayLayout.Resize(monitor,original,x,y,x*40,y*30,560,420,0);
                Near(corner.WidthDip,660,"corner width "+x+","+y);Near(corner.HeightDip,590,"corner height "+x+","+y);
                Check((x<0?corner.Rect.Right==original.Right:corner.Rect.Left==original.Left)&&(y<0?corner.Rect.Bottom==original.Bottom:corner.Rect.Top==original.Top),"corner opposite anchor "+x+","+y);
            }
            var shrink=DisplayLayout.Resize(monitor,original,-1,-1,9999,9999,560,420,0);
            Check(shrink.Rect.Right==original.Right&&shrink.Rect.Bottom==original.Bottom,"minimum keeps anchor");
            Near(shrink.WidthDip,560,"minimum width");Near(shrink.HeightDip,420,"minimum height");
            var grow=DisplayLayout.Resize(monitor,Rect(16,16,636,576),1,1,int.MaxValue,int.MaxValue,560,420,16);
            Check(grow.Rect.Right==2544&&grow.Rect.Bottom==1376,"maximum respects work area and pinned margin");
            Check(grow.WidthDip>1400&&grow.HeightDip>1200,"continuous dimensions exceed former preset caps");
            var negative=Monitor(Rect(-1920,-100,0,980),96);
            var negativeResize=DisplayLayout.Resize(negative,Rect(-1700,100,-1000,700),-1,-1,-900,-900,560,420,0);
            Check(negativeResize.Rect.Left==-1920&&negativeResize.Rect.Top==-100,"negative monitor coordinates remain signed");
            Check(negativeResize.Rect.Right==-1000&&negativeResize.Rect.Bottom==700,"negative monitor opposite anchors");
            var dpi150=Monitor(Rect(-2560,-200,0,1240),144);
            var scaled=Rect(-1500,100,-570,940);
            var large=DisplayLayout.Resize(dpi150,scaled,-1,0,-150,0,560,420,0);
            Near(large.WidthDip,720,"150 percent physical cursor delta converts once");Near(large.HeightDip,560,"150 percent unchanged height");
            var tinyStep=DisplayLayout.Resize(dpi150,scaled,1,0,1,0,560,420,0);
            Near(tinyStep.WidthDip,620+2.0/3,"one physical pixel has no integer DIP quantization");
            var returned=DisplayLayout.Resize(dpi150,scaled,1,0,0,0,560,420,0);
            Near(returned.WidthDip,620,"returning to origin does not accumulate rounding drift");
            var minimum=DisplayLayout.Resize(dpi150,scaled,1,1,-999,-999,560,420,0);
            Near(minimum.WidthDip,560,"scaled min width");Near(minimum.HeightDip,420,"scaled min height");
            var small=Monitor(Rect(0,0,500,350),96);
            var constrained=DisplayLayout.Resize(small,original,1,1,200,200,560,420,0);
            Check(constrained.Rect.Left==0&&constrained.Rect.Top==0&&constrained.Rect.Right==500&&constrained.Rect.Bottom==350,"small displays prioritize visibility over minimum");
            var untouched=DisplayLayout.Resize(monitor,original,0,0,int.MaxValue,int.MinValue,560,420,0);
            Check(untouched.Rect.Left==original.Left&&untouched.Rect.Top==original.Top&&untouched.Rect.Right==original.Right&&untouched.Rect.Bottom==original.Bottom,"inactive axes ignore cursor movement");
            bool invalid=false;try{DisplayLayout.Resize(monitor,original,2,0,0,0,560,420,0);}catch(ArgumentOutOfRangeException){invalid=true;}
            Check(invalid,"invalid edges rejected");
            Console.WriteLine("PASS: "+assertions+" resize geometry assertions.");return 0;
        }
        catch(Exception error) { Console.Error.WriteLine(error);return 1; }
    }
}
