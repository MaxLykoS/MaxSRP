"""Compare a controlled Cycles bake with actual native atlas GPU data at shared UVs.

python compare_gi_whitebox.py --input <Unity export> [--cycles <Cycles output>]
No Unity, Blender or Library access. Native diag9 and geometric directional decode
are separate columns; no ratio is fitted to make either match.
"""
import argparse
import csv
from datetime import datetime, timezone
import hashlib
import json
import math
from pathlib import Path
import struct
import sys
import zlib

import numpy as np


Y = np.array([.2126,.7152,.0722])


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def load_json(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def exr_channels(path):
    def string(stream):
        result=bytearray()
        while True:
            byte=stream.read(1)
            if byte==b'\0':return result.decode('ascii')
            if not byte:raise ValueError('Truncated EXR header')
            result+=byte
    with path.open('rb') as stream:
        if stream.read(4)!=b'\x76\x2f\x31\x01':raise ValueError('Not OpenEXR')
        stream.read(4)
        while True:
            name=string(stream)
            if not name:break
            kind=string(stream);size=struct.unpack('<I',stream.read(4))[0];data=stream.read(size)
            if name=='channels':
                channels={};cursor=0
                while data[cursor]!=0:
                    end=data.index(b'\0',cursor);channel=data[cursor:end].decode('ascii');cursor=end+1
                    channels[channel]=struct.unpack_from('<i',data,cursor)[0];cursor+=16
                return channels
    raise ValueError('EXR has no channel format header')


def save_png(path,pixels):
    pixels=np.asarray(pixels,dtype=np.uint8)
    def chunk(name,data):
        return struct.pack('>I',len(data))+name+data+struct.pack('>I',zlib.crc32(name+data)&0xffffffff)
    height,width,_=pixels.shape
    raw=b''.join(b'\0'+row.tobytes() for row in pixels)
    path.write_bytes(b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR',struct.pack('>IIBBBBB',width,height,8,2,0,0,0))+
                     chunk(b'IDAT',zlib.compress(raw,6))+chunk(b'IEND',b''))


def atlas_face_mask(manifest,index,width,height):
    """UV-center coverage for display only; no atlas padding is a real surface."""
    mask=np.zeros((height,width),dtype=bool)
    for face in manifest['faces']:
        if face['atlas']!=index:continue
        uv=np.asarray(face['atlasUV'],dtype=np.float64)*[width,height]
        for triangle in np.asarray(face['triangles']).reshape(-1,3):
            p=uv[triangle]
            minimum=np.maximum(np.floor(p.min(axis=0)-.5).astype(int),0)
            maximum=np.minimum(np.ceil(p.max(axis=0)-.5).astype(int),[width-1,height-1])
            if np.any(maximum<minimum):continue
            yy,xx=np.mgrid[minimum[1]:maximum[1]+1,minimum[0]:maximum[0]+1]
            a=p[1]-p[0];b=p[2]-p[0];denom=a[0]*b[1]-a[1]*b[0]
            if abs(denom)<1e-10:raise ValueError('Degenerate exported UV triangle')
            dx=xx+.5-p[0,0];dy=yy+.5-p[0,1]
            u=(dx*b[1]-dy*b[0])/denom;v=(a[0]*dy-a[1]*dx)/denom
            mask[minimum[1]:maximum[1]+1,minimum[0]:maximum[0]+1]|=(u>=0)&(v>=0)&(u+v<=1)
    return mask


def preview(rgb,gain):
    value=np.maximum(rgb,0)*gain
    return (255*np.clip((value/(1+value))**(1/2.2),0,1)).astype('u1')


def bilinear(image, uv):
    uv=np.asarray(uv,dtype=np.float64)
    coordinates=uv*np.array([image.shape[1],image.shape[0]])-.5
    low=np.floor(coordinates).astype(np.int64);fraction=coordinates-low
    x0=np.clip(low[:,0],0,image.shape[1]-1);y0=np.clip(low[:,1],0,image.shape[0]-1)
    x1=np.clip(low[:,0]+1,0,image.shape[1]-1);y1=np.clip(low[:,1]+1,0,image.shape[0]-1)
    return ((image[y0,x0]*(1-fraction[:,0,None])+image[y0,x1]*fraction[:,0,None])*(1-fraction[:,1,None])+
            (image[y1,x0]*(1-fraction[:,0,None])+image[y1,x1]*fraction[:,0,None])*fraction[:,1,None])


def interior_room(positions, normal):
    result=np.full(len(positions),'outside',dtype=object)
    for room,xmin,xmax,center in [('A',-4.1,-.1,(-2.1,1.5,0)),('B',.1,4.1,(2.1,1.5,0))]:
        inside=((positions[:,0]>=xmin-1e-5)&(positions[:,0]<=xmax+1e-5)&
                (positions[:,1]>=-1e-5)&(positions[:,1]<=3+1e-5)&
                (positions[:,2]>=-2-1e-5)&(positions[:,2]<=2+1e-5))
        inward=(np.asarray(center)-positions)@normal>1e-5
        result[inside&inward]=room
    return result


def metrics(native, cycles):
    n=np.asarray(native,dtype=np.float64);c=np.asarray(cycles,dtype=np.float64)
    if not n.size:return {'samples':0}
    nl=n@Y;cl=c@Y
    nm=float(nl.mean());cm=float(cl.mean())
    return {'samples':len(n),'nativeMean':nm,'cyclesMean':cm,
            'nativeToCycles':nm/cm if cm>1e-12 else None,
            'absoluteLuminanceMAE':float(np.abs(nl-cl).mean()),
            'luminanceL1OverCycles':float(np.abs(nl-cl).sum()/cl.sum()) if cl.sum()>1e-12 else None,
            'nativeMax':float(nl.max()),'cyclesMax':float(cl.max())}


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--input',required=True,type=Path)
    parser.add_argument('--cycles',type=Path)
    parser.add_argument('--output',type=Path)
    parser.add_argument('--native-raw-domain',choices=('legacy_e_over_pi','irradiance'),default='legacy_e_over_pi',
                        help='irradiance applies the theoretical Lambert 1/pi conversion, never a fitted ratio')
    parser.add_argument('--native-raw-to-pipeline',type=float,default=None,
                        help='Actual production constant supplied by the caller; leave unset while native oracle is pending')
    parser.add_argument('--cycles-to-pipeline',type=float,default=.01,
                        help='K/HDR for the calibrated Cycles E/pi output (1000/100000)')
    args=parser.parse_args()
    source=args.input.resolve();cycles_folder=(args.cycles or source/'Cycles').resolve()
    native_conversion=1/math.pi if args.native_raw_domain=='irradiance' else 1.0
    expected_native_pipeline=args.cycles_to_pipeline*native_conversion
    if args.cycles_to_pipeline<=0 or not math.isfinite(args.cycles_to_pipeline):raise ValueError('Invalid Cycles pipeline conversion')
    if args.native_raw_to_pipeline is not None:
        if not math.isclose(args.native_raw_to_pipeline,expected_native_pipeline,rel_tol=1e-6,abs_tol=1e-12):
            raise ValueError('Production native constant disagrees with the explicit raw domain and K/HDR; refusing hidden scale')
    out=(args.output or (cycles_folder/'ComparisonPhysical' if args.native_raw_domain=='irradiance' else cycles_folder)).resolve()
    out.mkdir(parents=True,exist_ok=True)
    manifest=load_json(source/'manifest.json');state=load_json(cycles_folder/'cycles_state.json')
    calibration=load_json(cycles_folder/'light_unit_calibration.json')
    if not state.get('completed') or not calibration.get('valid'):
        raise ValueError('Cycles bake/calibration did not complete successfully')
    if state['source_manifest_sha256']!=sha(source/'manifest.json') or state['nativeRunId']!=manifest['nativeRunId']:
        raise ValueError('Cycles result belongs to another Unity export/native run')
    if state.get('receiver_color_included') or state['bake_type']!='DIFFUSE' or state['pass_filter']!=['INDIRECT']:
        raise ValueError('Not an indirect-only, receiver-color-off bake')
    if state.get('denoise') or state.get('margin')!=0 or state.get('world')!='black':
        raise ValueError('Reference energy/margin/denoising contract differs')
    textures={}
    images=[]
    protected_files=[source/'manifest.json',source/'source_native_validation.json',cycles_folder/'cycles_state.json',cycles_folder/'light_unit_calibration.json']
    protected_hashes={str(p):sha(p) for p in protected_files}
    for atlas in manifest['atlases']:
        i=atlas['index'];h,w=atlas['height'],atlas['width']
        for key in ('color','direction'):
            if sha(source/atlas[key+'Raw'])!=atlas[key+'SHA256']:
                raise ValueError('Unity GPU data SHA mismatch')
        native=np.fromfile(source/atlas['colorRaw'],dtype='<f2').reshape(h,w,4).astype(np.float64)
        direction=np.fromfile(source/atlas['directionRaw'],dtype='u1').reshape(h,w,4).astype(np.float64)/255
        cycles_record=next(a for a in state['atlases'] if a['index']==i)
        if sha(cycles_folder/cycles_record['raw'])!=cycles_record['rawSHA256']:
            raise ValueError('Cycles raw data SHA mismatch')
        cycles=np.fromfile(cycles_folder/cycles_record['raw'],dtype='<f4').reshape(h,w,4).astype(np.float64)
        channels=exr_channels(cycles_folder/cycles_record['exr'])
        for path in [source/atlas['colorRaw'],source/atlas['directionRaw'],cycles_folder/cycles_record['raw'],cycles_folder/cycles_record['exr']]:
            protected_hashes[str(path)]=sha(path)
        if any(channels.get(channel)!=2 for channel in ('R','G','B')):
            raise ValueError('Cycles EXR RGB is not float32: '+str(channels))
        if not np.isfinite(native).all() or not np.isfinite(cycles).all() or (native[:,:,:3]<0).any() or (cycles[:,:,:3]<-1e-7).any():
            raise ValueError('Non-finite/negative energy in reference or native')
        textures[i]=(native,direction,cycles)
        valid=atlas_face_mask(manifest,i,w,h)&(cycles[:,:,3]>.99)
        native_display=native[:,:,:3]*native_conversion
        positive=np.concatenate((native_display[valid].ravel(),cycles[:,:,:3][valid].ravel()))
        positive=positive[positive>0]
        gain=.7/max(float(np.quantile(positive,.99)) if len(positive) else 0,1e-12)
        panels=[]
        for rgb in (native_display,cycles[:,:,:3],np.abs(native_display-cycles[:,:,:3])):
            display=preview(rgb,gain);display[~valid]=0;panels.append(display[::-1])
        gap=np.full((h,8,3),220,dtype='u1')
        image_name=f'atlas_{i}_native_{args.native_raw_domain}_cycles_difference.png'
        save_png(out/image_name,np.concatenate((panels[0],gap,panels[1],gap,panels[2]),axis=1))
        images.append({'image':image_name,'gain':gain,'validSurfacePixels':int(valid.sum()),
                       'panels':['native stored RGB'+(' / pi' if args.native_raw_domain=='irradiance' else ''),'Cycles calibrated E/pi','absolute RGB difference'],
                       'mask':'Only actual UV triangle center coverage with complete Cycles alpha; padding excluded from display',
                       'exrChannels':channels})
    rows=[];face_reports=[];groups={room:[[],[],[]] for room in ('A','B','outside')}
    for face in manifest['faces']:
        native_tex,dir_tex,cycle_tex=textures[face['atlas']]
        uv=np.asarray(face['atlasUV'],dtype=np.float64);p=np.asarray(face['positionsWS'],dtype=np.float64)
        normal=np.asarray(face['normalsWS'][0],dtype=np.float64);normal/=np.linalg.norm(normal)
        # Same 8x8 central-half sample positions as Unity native_validation, plus exact center.
        fractions=[(.5,.5)]+[(.25+.5*(x+.5)/8,.25+.5*(y+.5)/8) for y in range(8) for x in range(8)]
        # The shared floor/roof/back center is buried in the partition. Add valid room centers.
        if face['renderer'] in ('Floor_PY','Roof_NY','OuterBack_NZ'):
            a=np.column_stack((p[1]-p[0],p[3]-p[0]))
            for room_x in (-2.1,2.1):
                target=p.mean(axis=0);target[0]=room_x
                q=np.linalg.lstsq(a,target-p[0],rcond=None)[0]
                if (q>0).all() and (q<1).all():fractions.append(tuple(q))
        fraction=np.asarray(fractions)
        coords=uv[0]+fraction[:,0,None]*(uv[1]-uv[0])+fraction[:,1,None]*(uv[3]-uv[0])
        positions=p[0]+fraction[:,0,None]*(p[1]-p[0])+fraction[:,1,None]*(p[3]-p[0])
        n=bilinear(native_tex,coords);d=bilinear(dir_tex,coords);c=bilinear(cycle_tex,coords)
        factor=np.maximum((d[:,:3]-.5)@normal+.5,0)/np.maximum(d[:,3],1e-4)
        reconstructed=n[:,:3]*factor[:,None]
        rooms=interior_room(positions,normal)
        for k,(position,coord,room) in enumerate(zip(positions,coords,rooms)):
            if room!='outside' and c[k,3]<.99:
                raise ValueError('An interior comparison point has incomplete Cycles bake coverage: '+face['renderer'])
            row={'renderer':face['renderer'],'sample':k,'room':room,'atlas':face['atlas'],
                 'u':float(coord[0]),'v':float(coord[1]),'x':float(position[0]),'y':float(position[1]),'z':float(position[2]),
                 'nativeRawDiag9':float(n[k,:3]@Y),'nativeGeomDirectional':float(reconstructed[k]@Y),
                 'nativeEOverPi':float(n[k,:3]@Y)*native_conversion,
                 'nativeGeomEOverPi':float(reconstructed[k]@Y)*native_conversion,
                 'nativePipelineHDR':float(reconstructed[k]@Y)*args.native_raw_to_pipeline if args.native_raw_to_pipeline is not None else None,
                 'cyclesPipelineHDR':float(c[k,:3]@Y)*args.cycles_to_pipeline,
                 'cyclesEOverPi':float(c[k,:3]@Y),'directionFactor':float(factor[k]),'cyclesAlpha':float(c[k,3])}
            rows.append(row)
            # Primary aggregate uses interior points only. Exterior/buried quads stay inspectable.
            groups[room][0].append(n[k,:3]);groups[room][1].append(reconstructed[k]);groups[room][2].append(c[k,:3])
        mask=rooms!='outside'
        face_reports.append({'renderer':face['renderer'],'centerRoom':str(rooms[0]),
                             'centerNativeRaw':float(n[0,:3]@Y),'centerNativeGeomDirectional':float(reconstructed[0]@Y),
                             'centerNativeEOverPi':float(n[0,:3]@Y)*native_conversion,
                             'centerNativeGeomEOverPi':float(reconstructed[0]@Y)*native_conversion,
                             'centerCyclesEOverPi':float(c[0,:3]@Y),'centerDirectionalFactor':float(factor[0]),
                             'interiorRawDiag9':metrics(n[mask,:3],c[mask,:3]),
                             'interiorGeomDirectional':metrics(reconstructed[mask],c[mask,:3]),
                             'interiorPhysicalEOverPi':metrics(n[mask,:3]*native_conversion,c[mask,:3]),
                             'interiorPhysicalGeomEOverPi':metrics(reconstructed[mask]*native_conversion,c[mask,:3]),
                             'sourceMetaAlbedo':face['metaAlbedo']})
    report={'utc':datetime.now(timezone.utc).isoformat(),'input':str(source),'cycles':str(cycles_folder),'derivedOutput':str(out),
            'nativeRunId':manifest['nativeRunId'],'sourceManifestSHA256':sha(source/'manifest.json'),
            'validInputAndCalibration':True,'scope':'Shared actual atlas UV bilinear samples. No receiver albedo in either measurement. Native diag9 raw and geometric directional reconstruction are separate, with no fitted scale.',
            'units':'CSV retains native stored RGB, calibrated Cycles E/pi, and separately named native E/pi columns using the explicitly selected raw domain. Never fitted to an image ratio. Ng directional decode is reported separately.',
            'nativeRawDomain':args.native_raw_domain,'nativeRawToEOverPi':native_conversion,
            'conversionReason':'Lambert hemisphere normalization 1/pi; caller selected native raw irradiance domain' if args.native_raw_domain=='irradiance' else 'Legacy assumption raw already equals E/pi; retained for old behavior',
            'conversionIsFitted':False,'theoreticalPi':math.pi,
            'nativeRawToPipeline':args.native_raw_to_pipeline,'theoreticalNativeRawToPipeline':expected_native_pipeline,
            'cyclesToPipeline':args.cycles_to_pipeline,
            'pipelineConstantConfirmedByCaller':args.native_raw_to_pipeline is not None,
            'nativeToSceneHDR':manifest['nativeToSceneHDR'],'roomMetrics':{},'faces':face_reports,
            'atlasPreviews':images,
            'limitations':['No statement about production BRDF, sky energy, TAA, native denoiser quality or mobile performance.',
                           'Native/Cycles bounce termination semantics and reconstruction/filtering can differ. Raw reference uses margin=0 and compares only interior chart samples.',
                           'The midpoint of shared floor/roof/back lies under the separating wall and is excluded from room aggregates.']}
    for room,values in groups.items():
        report['roomMetrics'][room]={'rawDiag9':metrics(values[0],values[2]),'geomDirectional':metrics(values[1],values[2]),
                                    'physicalEOverPi':metrics(np.asarray(values[0])*native_conversion,values[2]),
                                    'physicalGeomEOverPi':metrics(np.asarray(values[1])*native_conversion,values[2])}
    a=report['roomMetrics']['A'];b=report['roomMetrics']['B']
    report['isClosed']=Path(manifest['scene']).stem=='GIWhiteboxClosed'
    report['closedRoomLeakageMeasurements']={
        'nativeRawBOverA':b['rawDiag9'].get('nativeMean',0)/max(a['rawDiag9'].get('nativeMean',0),1e-30),
        'nativeGeomBOverA':b['geomDirectional'].get('nativeMean',0)/max(a['geomDirectional'].get('nativeMean',0),1e-30),
        'cyclesBOverA':b['rawDiag9'].get('cyclesMean',0)/max(a['rawDiag9'].get('cyclesMean',0),1e-30)}
    # Numerical screening is explicit; an unexpected leak never becomes an unexplained PASS.
    errors=[]
    if a['rawDiag9'].get('cyclesMean',0)<=1e-12:errors.append('Room A reference is black: missing artificial bounce')
    if report['isClosed'] and max(report['closedRoomLeakageMeasurements'].values())>.005:
        errors.append('Closed room B mean exceeds explicit 0.5% of room A screening threshold; investigate instead of accepting')
    report['screeningErrors']=errors;report['screeningValid']=not errors
    write_path=out/'native_cycles_comparison.json'
    write_path.write_text(json.dumps(report,indent=2,allow_nan=False),encoding='utf-8')
    with (out/'native_cycles_uv_points.csv').open('w',newline='',encoding='utf-8') as stream:
        writer=csv.DictWriter(stream,fieldnames=list(rows[0]));writer.writeheader();writer.writerows(rows)
    lines=[f"Scene: {manifest['scene']}",f"Native run: {manifest['nativeRunId']}",f"Cycles samples: {state['samples']}; no denoise/clamp/margin; calibration passed.",
           'Native raw domain='+args.native_raw_domain+'; explicit nativeRawToEOverPi='+str(native_conversion)+'; fitted=False',
           'Actual nativeRawToPipeline='+str(args.native_raw_to_pipeline)+'; CyclesToPipeline='+str(args.cycles_to_pipeline),
           'Room | raw native | geom-direction native E/pi | Cycles E/pi | raw native/Cycles | physical native/Cycles']
    for room in ('A','B'):
        raw=report['roomMetrics'][room]['rawDiag9'];geom=report['roomMetrics'][room]['geomDirectional']
        physical=report['roomMetrics'][room]['physicalGeomEOverPi']
        lines.append(f"{room} | {raw.get('nativeMean')} | {physical.get('nativeMean')} | {raw.get('cyclesMean')} | {geom.get('nativeToCycles')} | {physical.get('nativeToCycles')}")
    lines.extend(['Closed leakage measurements: '+json.dumps(report['closedRoomLeakageMeasurements']),
                  'Screening: '+('ERROR: '+'; '.join(errors) if errors else 'passed stated input/isolation checks; physical agreement remains numerical measurements above')])
    (out/'native_cycles_comparison.txt').write_text('\n'.join(lines)+'\n',encoding='utf-8')
    html=['<!doctype html><html><meta charset="utf-8"><title>GI Whitebox Native / Cycles</title>',
          '<style>body{font:16px sans-serif;margin:32px;color:#222}img{max-width:100%;border:1px solid #bbb}table{border-collapse:collapse}th,td{padding:6px 12px;border:1px solid #bbb;text-align:right}th:first-child,td:first-child{text-align:left}pre{white-space:pre-wrap}</style>',
          '<h1>'+Path(manifest['scene']).stem+' · native / Cycles</h1><pre>'+'\n'.join(lines)+'</pre>',
          '<p>All atlas panels share one visualization gain. Left: native'+(' / pi' if args.native_raw_domain=='irradiance' else ' raw')+'; middle: calibrated Cycles irradiance/pi; right: absolute difference. No fitted scale. Unused padding excluded. PNG display is Reinhard + gamma; statistics use raw floats.</p>']
    for image in images:html.append('<figure><img src="'+image['image']+'"><figcaption>Shared gain '+str(image['gain'])+'</figcaption></figure>')
    html.append('<h2>Actual face centers (buried/exterior centers excluded from room aggregates)</h2><table><tr><th>Face</th><th>Room</th><th>Native raw</th><th>Native geom-N E/pi</th><th>Cycles E/pi</th><th>Dir factor</th></tr>')
    for face in face_reports:
        if face['centerRoom']=='outside':continue
        html.append('<tr><td>'+face['renderer']+'</td><td>'+face['centerRoom']+'</td>'+''.join('<td>'+format(face[key],'.7g')+'</td>' for key in ('centerNativeRaw','centerNativeGeomEOverPi','centerCyclesEOverPi','centerDirectionalFactor'))+'</tr>')
    html.append('</table><h2>Shared-gain interior wall crops</h2><p>Left native'+(' / pi' if args.native_raw_domain=='irradiance' else ' raw')+', right Cycles. One gain across all wall pairs in this case. Bilinear display resampling only; statistics retain actual shared UV point samples.</p>')
    crop_names=['OuterWest_PX','OuterFrontA_Lower_PZ','OuterFrontA_Upper_PZ','Partition_Upper_NX','OuterEast_NX','OuterFrontB_PZ','Partition_Upper_PX']
    crop_pairs=[];crop_values=[]
    q=(np.arange(256)+.5)/256*.95+.025;yy,xx=np.meshgrid(q,q,indexing='ij')
    for name in crop_names:
        matches=[f for f in manifest['faces'] if f['renderer']==name]
        if not matches:continue
        f=matches[0];uv=np.asarray(f['atlasUV']);coords=(uv[0]+xx.reshape(-1,1)*(uv[1]-uv[0])+yy.reshape(-1,1)*(uv[3]-uv[0]))
        native,_,cycles=textures[f['atlas']]
        n=bilinear(native,coords)[:,:3].reshape(256,256,3)*native_conversion
        c=bilinear(cycles,coords)[:,:3].reshape(256,256,3)
        crop_pairs.append((name,n,c));crop_values.extend([n.ravel(),c.ravel()])
    positive=np.concatenate(crop_values);positive=positive[positive>0]
    crop_gain=.7/max(float(np.quantile(positive,.99)) if len(positive) else 0,1e-12)
    contact=[]
    for name,n,c in crop_pairs:
        pair=np.concatenate((preview(n,crop_gain)[::-1],np.full((256,8,3),220,dtype='u1'),preview(c,crop_gain)[::-1]),axis=1)
        save_png(out/(name+'_native_cycles.png'),pair)
        contact.extend([pair,np.full((8,pair.shape[1],3),220,dtype='u1')])
        html.append('<h3>'+name+'</h3><img src="'+name+'_native_cycles.png">')
    if contact:
        save_png(out/'interior_wall_contact_sheet.png',np.concatenate(contact,axis=0))
    report['wallCropPreview']={'gain':crop_gain,'faces':[n for n,_,_ in crop_pairs],
                              'image':'interior_wall_contact_sheet.png','left':'native / pi' if args.native_raw_domain=='irradiance' else 'native raw','right':'Cycles E/pi'}
    html.append('<p>Detailed coordinates and values: native_cycles_uv_points.csv. Exact run IDs, hashes and caveats: native_cycles_comparison.json.</p></html>')
    (out/'native_cycles_comparison.html').write_text('\n'.join(html),encoding='utf-8')
    changed=[path for path,digest in protected_hashes.items() if sha(Path(path))!=digest]
    if changed:raise ValueError('Read-only comparison detected changed source data: '+str(changed))
    report['sourceDataHashesUnchanged']=True;report['protectedSourceSHA256']=protected_hashes
    write_path.write_text(json.dumps(report,indent=2,allow_nan=False),encoding='utf-8')
    print('\n'.join(lines))
    return 2 if errors else 0


if __name__=='__main__':
    sys.exit(main())
